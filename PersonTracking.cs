using System.Diagnostics;
using System.Text.Json;

public sealed class PersonTracking {
    public static async Task<(string[] Paths, string[] PlainPaths, double[] Timestamps, string Evidence)> Run(
        string root, string folder, double fps, CancellationToken ct) {
        var python = Environment.GetEnvironmentVariable("CCTV_YOLO_PYTHON") ??
            Path.GetFullPath(Path.Combine(root, "../../work/yolo-env/bin/python"));
        var weights = Path.Combine(root, "tracking/yolov8n.pt");
        if (!File.Exists(python) || !File.Exists(weights))
            throw new UserError("Person tracking is not installed. Install the tracking dependencies to filter frames containing people.", 503);
        var manifest = Path.Combine(folder, "tracking-input.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new {
            video = Path.Combine(folder, "video"), fps, max_frames = VideoTools.MaxFrames
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
        var records = doc.RootElement.EnumerateArray().ToArray();
        if (records.Length > VideoTools.MaxFrames) throw new UserError("Person detection exceeded the frame cap.", 502);
        records = records.Where(r => r.GetProperty("people").GetArrayLength() > 0).ToArray();
        return (records.Select(r => r.GetProperty("path").GetString()!).ToArray(), records.Select(r => r.GetProperty("plain_path").GetString()!).ToArray(), records.Select(r => r.GetProperty("timestamp").GetDouble()).ToArray(),
            JsonSerializer.Serialize(records.Select(r => new {
                timestamp = r.GetProperty("timestamp").GetDouble(), people = r.GetProperty("people").Clone()
            })));
    }
}
