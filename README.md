# CCTV Description Demo — ASP.NET Core

A local .NET 10 app with a simple video upload UI, persistent queue, preview, descriptions, cancellation/retry, and text export. Max 5 minutes and 500 MB per video. One model request at a time.

## Start on this Mac

Double-click `Start Demo.command`. The script uses the .NET SDK and Ollama runtime downloaded into this workspace. Open http://127.0.0.1:8766.

For normal development with .NET 10 installed globally, use `dotnet run`. Set `CCTV_FFMPEG` to an FFmpeg executable when running outside this workspace. The default binding is loopback only. `CCTV_URL` overrides it; `CCTV_DATA_DIR` changes the local storage directory. API credentials, if required, come from the `CCTV_API_KEY` environment variable and are not saved to the browser.

## Model modes

- **This Mac:** Ollama, `http://127.0.0.1:11434/v1`, model `qwen3-vl:4b-cctv` (Qwen3-VL 4B with a 16,384-token context for up to 10 frames). This smaller model is not equivalent to Gemma 4 12B. No inference data leaves the Mac in this mode.
- **Notebook GPU:** Enter the running notebook's root share URL. The app sends the video to `/api/describe`. This is the path for retaining the notebook's actual model and inference code; the remote session must remain running. Use only a trusted endpoint.

## Notebook alignment

`activity-prompt.txt` contains the user-authored eyewitness narrative prompt. It is read in full for each local generation and included as the final text part of the model request. Local mode uses images followed by the prompt, 600 output tokens, greedy-style temperature 0, and thinking disabled. It samples by a frame-index stride of round(FPS × 2), downsizes to a maximum 640 pixels, then evenly subsamples to a maximum of 10 frames.

The rest of this pipeline corresponds to the notebook's **JSON API path**, which does not apply YOLO filtering. Its batch path does apply YOLO; matching that experiment requires changing the notebook API to call the batch extraction/resilient-generation functions. The Gradio demo also has a stale default of 30 frames. Local FFmpeg decoding/resizing, FPS metadata, Ollama quantization, and image processing can differ from OpenCV/Transformers. Identical prompts do not guarantee identical descriptions. Local parsing marks malformed JSON as a failure and saves raw text instead of salvaging a truncated description.

## Files

- `Program.cs`: HTTP API, configuration, and durable queue.
- `DescriptionWorker.cs`: background queue processing and model adapters.
- `VideoTools.cs`: validation, preview images, frame extraction.
- `wwwroot/`: plain HTML/CSS/JavaScript UI.
- `data/`: uploaded videos, generated frames, queue state (created at runtime).

The app pauses the queue at restart. Interrupted jobs return to queued status. Cancelling a remote notebook request stops waiting locally; it does not guarantee cancellation of GPU work on the remote server.

## Verified locally

- .NET 10 build: no warnings or errors.
- Real Gemma 4 E2B request: completed on a 12-second synthetic clip with six frames in 91.1 seconds, including cold model startup. This verifies integration, not CCTV description quality.
- Integration checks: exact copied prompt, model request settings, 10-frame subsampling, 300-second acceptance/301-second rejection, invalid videos, playback ranges, pause/cancel/retry, malformed model output, persisted queue/restart, and notebook multipart uploads. The remote notebook adapter was tested against a local test server; a live Kaggle session was not connected.

## Folder uploads

Use **Add folder** to select a folder and queue its videos, including videos in subfolders. Files are added in filename order; non-video files are skipped. Each video still has a five-minute / 500 MB limit. Failed imports are listed below the toolbar. This imports the selected files once; it does not watch the folder for future changes.

## Person labels

Settings → Person labels (YOLO) enables local YOLOv8n + ByteTrack preprocessing. It tracks consecutive decoded frames on CPU, then sends at most 10 labeled frames with timestamps and normalized detection boxes to the model. Tracker IDs are anonymous estimates, can switch, and are not proof of actions or intent. The user-authored prompt is sent in full; detection guidance is added separately. Disable the option for plain-frame comparisons. Remote notebook mode does not use this local step.

The tracking subprocess exits before LLM generation to release its memory. Tracking can add latency, especially for long videos. Cancellation stops the subprocess. Dependencies live in ../../work/yolo-env; override CCTV_YOLO_PYTHON if needed. Weights: tracking/yolov8n.pt. No accuracy improvement is claimed without a comparison on real clips.

Person IDs appear only in frames containing multiple detected people. Single-person frames retain the box and timestamp without an ID. Untracked people in multi-person frames receive frame-local labels (for example F2P1), not persistent identities.

Local generation always filters the sampled frames through YOLO, even with labels disabled. Only sampled frames with person detections are sent (at most 10); timestamps reflect the retained frames. If none qualify, generation is skipped with an explanatory message and no LLM request. This filtering does not modify the remote notebook backend.

## Updated sampling and narrative settings

YOLO now runs once per second. Samples without detected people are discarded before selection. Up to 150 qualifying frames are selected evenly in chronological order. Labels distinguish multiple detections within each image only; sparse samples are not used to claim persistent person identity. The prompt merges repeated actions, limits the narrative to 200 words, and uses a sensitive, observable-cue-based suspicion threshold. A large image input can exceed the configured model context; the app surfaces that error rather than silently dropping frames.

## Bounded model requests
Selected frames are now processed in chronological batches of at most six images. Context-size rejections recursively split only the rejected batch; no selected images are silently dropped. Each request receives the full prompt and a rolling narrative summary. Any positive suspicious/weapon flag is retained across batches. Raw batch results are saved beside the video in batch-responses.json. Summarization may lose detail or carry forward model mistakes; this change addresses request size, not verified accuracy.
