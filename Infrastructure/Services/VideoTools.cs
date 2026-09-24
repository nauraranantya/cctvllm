using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

public sealed class VideoTools(IWebHostEnvironment env) {
    public const int MaxFrames = 150;
    public const long MaxBytes = 500L * 1024 * 1024;
    private readonly string ffmpeg = Environment.GetEnvironmentVariable("CCTV_FFMPEG") ??
        Path.GetFullPath(Path.Combine(env.ContentRootPath, "../../work/demo-deps/imageio_ffmpeg/binaries/ffmpeg-macos-aarch64-v7.1"));

    private async Task<(int Code, string Error)> Run(string[] args, CancellationToken cancellationToken) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg) {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try { process.Start(); }
        catch (Exception) { throw new UserError("FFmpeg is unavailable. Set CCTV_FFMPEG to its executable path.", 503); }
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await stdout;
        return (process.ExitCode, await stderr);
    }

    public async Task<(double Duration, double Fps)> Inspect(string path, CancellationToken ct) {
        var result = await Run(["-hide_banner", "-i", path], ct);
        var durationMatch = Regex.Match(result.Error, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        var videoLine = result.Error.Split('\n').FirstOrDefault(x => x.Contains("Video:"));
        if (!durationMatch.Success || videoLine is null) throw new UserError("This file is not a readable video.", 422);
        double Part(int n) => double.Parse(durationMatch.Groups[n].Value, CultureInfo.InvariantCulture);
        var duration = Part(1) * 3600 + Part(2) * 60 + Part(3);
        if (duration <= 0 || duration > 300) throw new UserError(duration > 300 ? "Video exceeds the 5-minute limit." : "Video duration is invalid.", 422);
        var fpsMatch = Regex.Match(videoLine, @"([\d.]+) fps");
        var fps = fpsMatch.Success ? double.Parse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 25;
        if (fps <= 0 || fps > 240) fps = 25;
        return (duration, fps);
    }

    public async Task Poster(string input, string output, CancellationToken ct) {
        var r = await Run(["-y", "-i", input, "-frames:v", "1", "-vf", "scale=320:-2", "-pix_fmt", "yuvj420p", output], ct);
        if (r.Code != 0 || !File.Exists(output)) throw new UserError("Could not decode this video.", 422);
    }

    public async Task<(string[] Paths, double[] Timestamps)> Frames(string folder, double fps, CancellationToken ct) {
        var output = Path.Combine(folder, "frames");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);
        // Matches the notebook's frame-index stride and full-duration even subsampling.
        var stride = Math.Max(1, (int)Math.Round(fps * 2, MidpointRounding.ToEven));
        var filter = $"select=not(mod(n\\,{stride})),scale=w='if(gte(iw,ih),min(640,iw),trunc(iw*min(1,640/ih)))':h='if(gte(iw,ih),trunc(ih*min(1,640/iw)),min(640,ih))':flags=area";
        var r = await Run(["-y", "-i", Path.Combine(folder, "video"), "-vf", filter, "-fps_mode", "vfr", Path.Combine(output, "%04d.png")], ct);
        var all = Directory.GetFiles(output, "*.png").Order().ToArray();
        if (r.Code != 0 || all.Length == 0) throw new UserError("Could not extract video frames.", 422);
        var indices = all.Length <= MaxFrames ? Enumerable.Range(0, all.Length).ToArray() :
            Enumerable.Range(0, MaxFrames).Select(i => (int)Math.Round(i * (all.Length - 1d) / (MaxFrames - 1), MidpointRounding.ToEven)).Distinct().ToArray();
        return (indices.Select(i => all[i]).ToArray(), indices.Select(i => i * stride / fps).ToArray());
    }
}
