using System.Diagnostics;
using System.Text.Json;

public sealed class PersonTracking {
    // Detection runs denser than the frames actually sent to the vision model: motion is
    // only measurable with closely spaced samples, while each frame forwarded to the model
    // costs roughly a thousand vision tokens.
    public const double SampleInterval = 0.5;   // seconds between detections (2 fps)
    public const double ModelMinGap = 1.0;      // minimum seconds between frames sent onward

    public static async Task<(string[] Paths, string[] PlainPaths, double[] Timestamps, string Evidence, string Motion)> Run(
        string root, string folder, string videoPath, double fps, CancellationToken ct) {
        var python = Environment.GetEnvironmentVariable("CCTV_YOLO_PYTHON") ??
            Path.GetFullPath(Path.Combine(root, "../../work/yolo-env/bin/python"));
        var weights = Path.Combine(root, "tracking/yolov8n.pt");
        if (!File.Exists(python) || !File.Exists(weights))
            throw new UserError("Person tracking is not installed. Install the tracking dependencies to filter frames containing people.", 503);
        if (!File.Exists(videoPath))
            throw new UserError("This video is no longer at its original path in the linked folder.", 404);
        var manifest = Path.Combine(folder, "tracking-input.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new {
            // May sit outside the job folder: a linked video is read where the user keeps it.
            video = videoPath, fps, max_frames = VideoTools.MaxFrames,
            sample_interval = SampleInterval, model_min_gap = ModelMinGap
        }), ct);
        using var process = new Process { StartInfo = new ProcessStartInfo(python) {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = root
        }};
        foreach (var arg in new[] { Path.Combine(root, "tracking/track_people.py"), manifest, weights })
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "2";
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            throw new UserError("Person tracking timed out. Try a shorter video.", 504);
        }
        await output;
        if (process.ExitCode != 0) {
            var detail = await error;
            throw new UserError("Person tracking failed: " + detail[^Math.Min(detail.Length, 350)..], 502);
        }
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "tracking-result.json"), ct));
        // The script returns {frames, motion}: the frames selected for the vision model, plus
        // a motion timeline covering every detection sample (denser than those frames). A bare
        // array is still accepted so a result file from an older run stays readable.
        var resultRoot = doc.RootElement;
        var framesElement = resultRoot.ValueKind == JsonValueKind.Object ? resultRoot.GetProperty("frames") : resultRoot;
        var motion = resultRoot.ValueKind == JsonValueKind.Object && resultRoot.TryGetProperty("motion", out var timeline)
            ? timeline.GetRawText() : "[]";
        var records = framesElement.EnumerateArray().ToArray();
        if (records.Length > VideoTools.MaxFrames) throw new UserError("Person detection exceeded the frame cap.", 502);
        records = records.Where(r => r.GetProperty("people").GetArrayLength() > 0).ToArray();
        return (records.Select(r => r.GetProperty("path").GetString()!).ToArray(), records.Select(r => r.GetProperty("plain_path").GetString()!).ToArray(), records.Select(r => r.GetProperty("timestamp").GetDouble()).ToArray(),
            JsonSerializer.Serialize(records.Select(r => new {
                timestamp = r.GetProperty("timestamp").GetDouble(), people = r.GetProperty("people").Clone()
            })), motion);
    }
}
