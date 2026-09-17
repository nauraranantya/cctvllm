"""Detect people once per second, then evenly select qualifying frames."""
import argparse, json, os
from pathlib import Path
os.environ.setdefault("YOLO_CONFIG_DIR", str(Path(__file__).resolve().parent / ".config"))
import cv2
from ultralytics import YOLO

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
    model = YOLO(args.weights)
    cap = cv2.VideoCapture(spec["video"])
    if not cap.isOpened():
        raise RuntimeError("Cannot open video for person detection")
    fps = spec["fps"]
    output = manifest.parent / "tracked"
    output.mkdir(exist_ok=True)
    records = []
    frame_index = second = 0
    try:
        while cap.grab():
            if frame_index != round(second * fps):
                frame_index += 1
                continue
            ok, frame = cap.retrieve()
            if not ok:
                raise RuntimeError("Could not decode sampled frame")
            timestamp = frame_index / fps
            second += 1
            frame_index += 1
            h,w = frame.shape[:2]
            scale = min(1,640/max(h,w))
            frame = cv2.resize(frame,(max(1,int(w*scale)),max(1,int(h*scale))))
            # Sparse samples do not establish persistent identity across seconds.
            result = model.predict(frame,classes=[0],conf=0.25,imgsz=640,device="cpu",verbose=False)[0]
            boxes = result.boxes
            if boxes is None or len(boxes)==0:
                continue
            raw_path = output / f"{second:04d}-plain.png"
            if not cv2.imwrite(str(raw_path),frame): raise RuntimeError("Cannot save frame")
            people = []
            for index,(box,confidence) in enumerate(zip(boxes.xyxy.tolist(),boxes.conf.tolist()),1):
                x1,y1,x2,y2 = map(int,box)
                label = f"F{second}P{index}" if len(boxes)>1 else None
                people.append({"id":label,"id_scope":"frame" if label else None,
                               "confidence":round(confidence,2),
                               "box":[round(x1/frame.shape[1],3),round(y1/frame.shape[0],3),
                                      round(x2/frame.shape[1],3),round(y2/frame.shape[0],3)]})
                cv2.rectangle(frame,(x1,y1),(x2,y2),(40,210,255),1)
                if label:
                    cv2.putText(frame,label,(x1,max(14,y1-4)),cv2.FONT_HERSHEY_SIMPLEX,.45,(40,210,255),1,cv2.LINE_AA)
            cv2.putText(frame,f"{timestamp:.2f}s",(6,18),cv2.FONT_HERSHEY_SIMPLEX,.5,(255,255,255),1,cv2.LINE_AA)
            path = output / f"{second:04d}.png"
            if not cv2.imwrite(str(path),frame): raise RuntimeError("Cannot save labeled frame")
            records.append({"path":str(path),"plain_path":str(raw_path),"timestamp":timestamp,"people":people})
    finally:
        cap.release()
    records = select_records(records,spec.get("max_frames",150))
    (manifest.parent/"tracking-result.json").write_text(json.dumps(records))
if __name__=="__main__":
    main()
