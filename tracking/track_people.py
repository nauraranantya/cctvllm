"""Detect people twice per second, estimate how fast each one is moving, then
evenly select a sparser set of qualifying frames to send to the vision model.

The detection rate and the model-frame rate are deliberately different. Motion
needs dense sampling to be measurable at all -- at one sample per second a
running person has already crossed the frame -- but every frame handed to the
vision model costs roughly a thousand vision tokens, so the frames actually sent
on stay at about one per second. The dense track is summarised as text instead.
"""
import argparse, json, math, os
from pathlib import Path
os.environ.setdefault("YOLO_CONFIG_DIR", str(Path(__file__).resolve().parent / ".config"))
import cv2
from ultralytics import YOLO

SAMPLE_INTERVAL = 0.5       # seconds between detections (2 fps)
MODEL_MIN_GAP = 1.0         # minimum seconds between frames sent to the vision model
MATCH_MAX_GAP = 1.5         # stop linking a person across a gap longer than this
MATCH_MAX_TRAVEL = 3.0      # body-heights of travel allowed between two sightings
MATCH_SIZE_RATIO = 2.0      # reject a match when box height changes by more than this factor

# Speeds are in body-heights per second: the person's own bounding-box height is
# the yardstick, which cancels out how near or far they are from the camera. A
# normal walk is about 1.4 m/s against a ~1.7 m frame, so roughly 0.8 of these
# units; a run is upwards of 2.5. These bands are heuristics, not measurements.
MOTION_BANDS = ((0.25, "still"), (1.2, "walking"), (2.2, "hurrying"))
MOTION_FASTEST = "running"


def classify(speed):
    for limit, name in MOTION_BANDS:
        if speed < limit:
            return name
    return MOTION_FASTEST


def associate(previous, current, dt):
    """Greedy nearest-neighbour matching between two consecutive samples.

    Deliberately not ultralytics' ByteTrack: that needs an extra `lap` package
    which ultralytics tries to pip-install on first use, so a missing wheel
    would turn into a hard failure of the whole pipeline. At 2 fps with a
    handful of people, matching on centre distance measured in body-heights is
    accurate enough to separate walking from running, and cannot fail to import.
    """
    if not previous or dt <= 0 or dt > MATCH_MAX_GAP:
        return {}
    pairs = []
    for i, old in enumerate(previous):
        for j, new in enumerate(current):
            if not new["height"]:
                continue
            ratio = old["height"] / new["height"]
            if not (1 / MATCH_SIZE_RATIO) <= ratio <= MATCH_SIZE_RATIO:
                continue
            height = (old["height"] + new["height"]) / 2
            travel = math.dist(old["center"], new["center"]) / height
            if travel <= MATCH_MAX_TRAVEL:
                pairs.append((travel, i, j))
    pairs.sort()
    matched, used_old, used_new = {}, set(), set()
    for travel, i, j in pairs:
        if i in used_old or j in used_new:
            continue
        used_old.add(i)
        used_new.add(j)
        matched[j] = travel / dt
    return matched


def select_records(records, limit):
    if len(records) <= limit:
        return records
    return [records[round(i * (len(records)-1) / (limit-1))] for i in range(limit)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("manifest")
    parser.add_argument("weights")
    args = parser.parse_args()
    manifest = Path(args.manifest)
    spec = json.loads(manifest.read_text())
    interval = float(spec.get("sample_interval", SAMPLE_INTERVAL))
    model_gap = float(spec.get("model_min_gap", MODEL_MIN_GAP))
    model = YOLO(args.weights)
    cap = cv2.VideoCapture(spec["video"])
    if not cap.isOpened():
        raise RuntimeError("Cannot open video for person detection")
    fps = spec["fps"]
    output = manifest.parent / "tracked"
    output.mkdir(exist_ok=True)
    records, motion = [], []
    previous, previous_time = [], None
    frame_index = sample = 0
    try:
        while cap.grab():
            if frame_index != round(sample * fps * interval):
                frame_index += 1
                continue
            ok, frame = cap.retrieve()
            if not ok:
                raise RuntimeError("Could not decode sampled frame")
            timestamp = frame_index / fps
            sample += 1
            frame_index += 1
            h,w = frame.shape[:2]
            scale = min(1,640/max(h,w))
            frame = cv2.resize(frame,(max(1,int(w*scale)),max(1,int(h*scale))))
            # Sparse samples do not establish persistent identity across seconds.
            result = model.predict(frame,classes=[0],conf=0.25,imgsz=640,device="cpu",verbose=False)[0]
            boxes = result.boxes
            if boxes is None or len(boxes)==0:
                previous, previous_time = [], None
                continue
            # Measure motion from the raw detections, before anything is drawn on the frame.
            current = [{"center": ((x1+x2)/2, (y1+y2)/2), "height": max(1, y2-y1)}
                       for x1,y1,x2,y2 in (map(int,box) for box in boxes.xyxy.tolist())]
            matched = associate(previous, current,
                                timestamp - previous_time if previous_time is not None else 0)
            raw_path = output / f"{sample:04d}-plain.png"
            if not cv2.imwrite(str(raw_path),frame): raise RuntimeError("Cannot save frame")
            people = []
            for index,(box,confidence) in enumerate(zip(boxes.xyxy.tolist(),boxes.conf.tolist()),1):
                x1,y1,x2,y2 = map(int,box)
                speed = matched.get(index-1)
                pace = classify(speed) if speed is not None else None
                label = f"F{sample}P{index}" if len(boxes)>1 else None
                people.append({"id":label,"id_scope":"frame" if label else None,
                               "confidence":round(confidence,2),
                               "speed":round(speed,2) if speed is not None else None,
                               "motion":pace,
                               "box":[round(x1/frame.shape[1],3),round(y1/frame.shape[0],3),
                                      round(x2/frame.shape[1],3),round(y2/frame.shape[0],3)]})
                cv2.rectangle(frame,(x1,y1),(x2,y2),(40,210,255),1)
                caption = " ".join(part for part in (label, pace) if part)
                if caption:
                    cv2.putText(frame,caption,(x1,max(14,y1-4)),cv2.FONT_HERSHEY_SIMPLEX,.45,(40,210,255),1,cv2.LINE_AA)
            paced = [p for p in people if p["speed"] is not None]
            fastest = max(paced, key=lambda p: p["speed"], default=None)
            motion.append({"timestamp":round(timestamp,2),"people":len(people),
                           "fastest":fastest["speed"] if fastest else None,
                           "motion":fastest["motion"] if fastest else None})
            cv2.putText(frame,f"{timestamp:.2f}s",(6,18),cv2.FONT_HERSHEY_SIMPLEX,.5,(255,255,255),1,cv2.LINE_AA)
            path = output / f"{sample:04d}.png"
            if not cv2.imwrite(str(path),frame): raise RuntimeError("Cannot save labeled frame")
            records.append({"path":str(path),"plain_path":str(raw_path),"timestamp":timestamp,"people":people})
            previous, previous_time = current, timestamp
    finally:
        cap.release()
    # Every detected sample feeds the motion timeline, but only a ~1/sec subset
    # is sent to the vision model, so doubling the detection rate does not
    # double the number of images (and vision tokens) per video.
    chosen, last = [], None
    for record in records:
        if last is None or record["timestamp"] - last >= model_gap:
            chosen.append(record)
            last = record["timestamp"]
    chosen = select_records(chosen,spec.get("max_frames",150))
    (manifest.parent/"tracking-result.json").write_text(json.dumps({"frames":chosen,"motion":motion}))
if __name__=="__main__":
    main()
