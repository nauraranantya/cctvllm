using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("CCTV_URL") ?? "http://127.0.0.1:8766");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = VideoTools.MaxBytes);
builder.Services.AddSingleton<QueueStore>();
builder.Services.AddSingleton<VideoTools>();
builder.Services.AddHttpClient("model", c => c.Timeout = TimeSpan.FromMinutes(15));
builder.Services.AddSingleton<DescriptionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DescriptionWorker>());
var app = builder.Build();
app.Use(async (ctx, next) => {
    if (ctx.Request.Method != "GET" && ctx.Request.Method != "HEAD" &&
        ctx.Request.Headers.Origin.Count > 0 && ctx.Request.Headers.Origin != $"{ctx.Request.Scheme}://{ctx.Request.Host}") {
        ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { detail = "Cross-origin requests are not allowed." }); return;
    }
    try { await next(ctx); }
    catch (Exception ex) when (!ctx.Response.HasStarted) {
        ctx.Response.StatusCode = ex is UserError u ? u.Status : ex is BadHttpRequestException b ? b.StatusCode : 500;
        await ctx.Response.WriteAsJsonAsync(new { detail = ex is UserError or BadHttpRequestException ? ex.Message : "Request failed. Check the server log." });
        if (ex is not UserError) app.Logger.LogError(ex, "Request failed");
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

string[] videoExtensions = [".mp4", ".mov", ".m4v", ".webm", ".avi", ".mkv", ".mpeg", ".mpg", ".mts", ".m2ts", ".wmv", ".3gp", ".ogv"];
var scanGate = new SemaphoreSlim(1, 1);

// The browser's folder picker only ever reports a folder *name*, never its path, so a linked
// folder is entered as text and read by the server. That is the whole point of the feature:
// this server runs on the same machine as the videos, so it can re-read the folder later on
// its own -- a picker selection is a one-time snapshot that cannot be refreshed.
string ResolveFolder(string raw) {
    var value = raw.Trim();
    if (value.StartsWith('~'))
        value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[1..].TrimStart('/'));
    if (!Path.IsPathRooted(value)) throw new UserError("Enter the folder's full path, starting with / or ~.");
    var full = Path.GetFullPath(value);
    if (!Directory.Exists(full)) throw new UserError("There is no folder at that path.", 404);
    return full;
}

async Task<object> ScanFolder(QueueStore store, VideoTools videos, CancellationToken ct) {
    var folder = store.LinkedFolder ?? throw new UserError("Link a folder first.");
    if (!Directory.Exists(folder)) throw new UserError("The linked folder is no longer there. Link it again.", 404);
    if (!await scanGate.WaitAsync(0, ct)) throw new UserError("A refresh is already running.", 409);
    try {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(p => videoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Where(p => !Path.GetFileName(p).StartsWith('.'))   // .DS_Store, macOS ._ resource forks
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var errors = new List<string>();
        int added = 0, skipped = 0;
        foreach (var path in files) {
            ct.ThrowIfCancellationRequested();
            if (store.AlreadyImported(path)) { skipped++; continue; }
            var label = Path.GetRelativePath(folder, path);
            try {
                var size = new FileInfo(path).Length;
                if (size > VideoTools.MaxBytes) throw new UserError("Maximum file size is 500 MB.");
                var info = await videos.Inspect(path, ct);
                var job = new VideoJob {
                    Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileName(path),
                    SourcePath = path, Duration = info.Duration, Fps = info.Fps, Bytes = size
                };
                var dir = store.Folder(job.Id); Directory.CreateDirectory(dir);
                try { await videos.Poster(path, Path.Combine(dir, "poster.jpg"), ct); store.Add(job); added++; }
                catch { Directory.Delete(dir, true); throw; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{label}: {(ex is UserError ? ex.Message : "could not be read.")}"); }
        }
        return new { linkedFolder = folder, added, skipped, errors };
    } finally { scanGate.Release(); }
}

app.MapGet("/api/state", (QueueStore s) => s.Snapshot());
app.MapGet("/api/health", () => new { ok = true, backend = "ASP.NET Core", version = Environment.Version.ToString() });
app.MapGet("/api/models", async (string? endpoint, IHttpClientFactory clients, CancellationToken ct) => {
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https") || !uri.IsLoopback ||
        !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        throw new UserError("Enter a localhost model server URL.", 400);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(8));
    try {
        using var client = clients.CreateClient("model");
        using var response = await client.GetAsync(endpoint!.TrimEnd('/') + "/models", timeout.Token);
        if (!response.IsSuccessStatusCode) throw new UserError("Could not list models from this server.", 502);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var models = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()).Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct().Order().ToArray();
        return Results.Ok(new { models });
    } catch (HttpRequestException) {
        throw new UserError("Cannot reach the model server. Start Ollama and try again.", 502);
    } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
        throw new UserError("Model list request timed out. Try again.", 504);
    }
});
app.MapPost("/api/settings", (ModelSettings value, QueueStore s) => {
    if (value.Mode is not ("local" or "notebook")) throw new UserError("Choose local model or notebook.");
    if (!Uri.TryCreate(value.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        throw new UserError("Enter a valid server URL without embedded credentials.");
    if (value.Mode == "local" && !uri.IsLoopback) throw new UserError("Local mode requires a localhost address.");
    if (value.Mode == "local" && string.IsNullOrWhiteSpace(value.Model)) throw new UserError("Enter a model name.");
    if ((value.FlagActivity?.Length ?? 0) > 4000 || (value.SiteContext?.Length ?? 0) > 4000)
        throw new UserError("Criteria and context must each be 4,000 characters or less.");
    s.SetSettings(value with { Endpoint = value.Endpoint.TrimEnd('/'), Model = value.Model.Trim() });
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/videos", async (HttpRequest request, QueueStore s, VideoTools videos, CancellationToken ct) => {
    var name = Path.GetFileName(Uri.UnescapeDataString(request.Headers["X-Filename"].FirstOrDefault() ?? "video.mp4"));
    if (name.Length > 200) name = name[..200];
    var job = new VideoJob { Id = Guid.NewGuid().ToString("N"), Name = name };
    var dir = s.Folder(job.Id); Directory.CreateDirectory(dir);
    try {
        long bytes = 0;
        await using (var file = File.Create(Path.Combine(dir, "video"))) {
            var buffer = new byte[81920]; int n;
            while ((n = await request.Body.ReadAsync(buffer, ct)) > 0) {
                bytes += n; if (bytes > VideoTools.MaxBytes) throw new UserError("Maximum file size is 500 MB.", 413);
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        var info = await videos.Inspect(Path.Combine(dir, "video"), ct);
        job = job with { Duration = info.Duration, Fps = info.Fps, Bytes = bytes };
        await videos.Poster(Path.Combine(dir, "video"), Path.Combine(dir, "poster.jpg"), ct);
        s.Add(job); return Results.Ok(job);
    } catch { Directory.Delete(dir, true); throw; }
});
app.MapPost("/api/folder", async (LinkRequest value, QueueStore s, VideoTools videos, CancellationToken ct) => {
    if (string.IsNullOrWhiteSpace(value.Path)) {
        s.SetLinkedFolder(null);
        return Results.Ok(new { linkedFolder = (string?)null, added = 0, skipped = 0, errors = new List<string>() });
    }
    var folder = ResolveFolder(value.Path);
    if (folder.Equals(s.Root, StringComparison.OrdinalIgnoreCase) ||
        folder.StartsWith(s.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new UserError("That is the app's own data folder. Choose the folder your videos are in.");
    s.SetLinkedFolder(folder);
    return Results.Ok(await ScanFolder(s, videos, ct));
});
app.MapPost("/api/folder/refresh", async (QueueStore s, VideoTools videos, CancellationToken ct) =>
    Results.Ok(await ScanFolder(s, videos, ct)));
app.MapGet("/api/videos/{id}/{asset}", (string id, string asset, QueueStore s) => {
    var job = s.Get(id);
    if (asset is not ("video" or "poster.jpg")) return Results.NotFound();
    var path = asset == "video" ? s.VideoPath(job) : Path.Combine(s.Folder(job.Id), asset);
    if (!File.Exists(path))
        throw new UserError(asset == "video" && job.SourcePath is not null
            ? "This video is no longer at its original path in the linked folder." : "File not found.", 404);
    var type = "image/jpeg";
    if (asset == "video" && !new FileExtensionContentTypeProvider().TryGetContentType(job.Name, out type)) type = "video/mp4";
    return Results.File(path, type, enableRangeProcessing: true);
});
app.MapPost("/api/queue/{action}", (string action, QueueStore s) => {
    if (action is not ("start" or "pause")) throw new UserError("Unknown queue action.");
    s.SetRunning(action == "start"); return Results.Ok(new { ok = true });
});
app.MapPost("/api/videos/{id}/{action}", (string id, string action, QueueStore s, DescriptionWorker worker) => {
    if (action == "cancel") worker.Cancel(id);
    else if (action == "retry") s.Retry(id);
    else if (action == "delete") s.Remove(id);
    else throw new UserError("Unknown video action.");
    return Results.Ok(new { ok = true });
});
app.Run();

public sealed class UserError(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
public sealed record ModelSettings(string Mode = "local", string Endpoint = "http://127.0.0.1:11434/v1", string Model = "gemma4:e2b-it-qat", bool PersonLabels = true) {
    public string FlagActivity { get; init; } = "";
    public bool SiteContextEnabled { get; init; }
    public string SiteContext { get; init; } = "";
    public bool WeaponEnabled { get; init; } = true;
}
public sealed record LinkRequest(string? Path);
public sealed record VideoJob {
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    // Set only for videos discovered in the linked folder. The file stays where the user keeps
    // it and is read in place; nothing is copied into data/, and removing the job never touches it.
    public string? SourcePath { get; init; }
    public string Status { get; init; } = "queued";
    public string Stage { get; init; } = "Queued";
    public double Duration { get; init; }
    public double Fps { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public string? Description { get; init; }
    public string? Suspicious { get; init; }
    public string? Weapon { get; init; }
    public bool WeaponEnabled { get; init; } = true;
    public string? Error { get; init; }
    public string? RawText { get; init; }
    public string? Model { get; init; }
    public string? BackendMode { get; init; }
    public bool PersonLabels { get; init; }
    public double[] Timestamps { get; init; } = [];
    public double? Elapsed { get; init; }
}
// LinkedFolder is a property rather than a constructor parameter so a state.json written
// before this feature existed still deserializes, with the folder simply absent.
public sealed record StoredState(ModelSettings Settings, List<VideoJob> Jobs) {
    public string? LinkedFolder { get; init; }
}
public sealed class QueueStore {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string root;
    private List<VideoJob> items = [];
    private ModelSettings settings = new();
    private string? linkedFolder;
    private bool running;
    public QueueStore(IWebHostEnvironment env) {
        root = Environment.GetEnvironmentVariable("CCTV_DATA_DIR") ?? Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(root, "state.json"))) {
            var saved = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(Path.Combine(root, "state.json")), Json)!;
            settings = saved.Settings;
            linkedFolder = saved.LinkedFolder;
            items = saved.Jobs.Select(j => j.Status == "processing" ? j with { Status = "queued", Stage = "Queued after restart" } : j).ToList();
        }
    }
    private void Persist() {
        File.WriteAllText(Path.Combine(root, "state.tmp"),
            JsonSerializer.Serialize(new StoredState(settings, items) { LinkedFolder = linkedFolder }, Json));
        File.Move(Path.Combine(root, "state.tmp"), Path.Combine(root, "state.json"), true);
    }
    public object Snapshot() { lock (gate) return new { jobs = items.ToArray(), running, settings, linkedFolder, frameLimit = VideoTools.MaxFrames }; }
    public string Root => root;
    public string? LinkedFolder { get { lock (gate) return linkedFolder; } }
    public void SetLinkedFolder(string? value) { lock (gate) { linkedFolder = value; Persist(); } }
    // Where this job's video actually lives: in the user's linked folder, or in the job's own
    // folder for an uploaded file. Everything downstream reads the video through here.
    public string VideoPath(VideoJob job) => job.SourcePath ?? Path.Combine(Folder(job.Id), "video");
    public bool AlreadyImported(string sourcePath) {
        lock (gate) return items.Any(j => j.SourcePath is not null &&
            string.Equals(j.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
    }
    public string Folder(string id) {
        if (!Guid.TryParseExact(id, "N", out _)) throw new UserError("Video not found.", 404);
        return Path.Combine(root, id);
    }
    public VideoJob Get(string id) { lock (gate) return items.FirstOrDefault(j => j.Id == id) ?? throw new UserError("Video not found.", 404); }
    public void Add(VideoJob job) { lock (gate) { items.Add(job); Persist(); } }
    public void Update(string id, Func<VideoJob, VideoJob> update) {
        lock (gate) { var index = items.FindIndex(j => j.Id == id); if (index >= 0) { items[index] = update(items[index]); Persist(); } }
    }
    public void SetSettings(ModelSettings value) {
        lock (gate) {
            if (items.Any(j => j.Status == "processing")) throw new UserError("Pause and wait for the current video before changing models.", 409);
            settings = value; Persist();
        }
    }
    public void SetRunning(bool value) { lock (gate) running = value; }
    public (VideoJob Job, ModelSettings Settings)? Take() {
        lock (gate) {
            if (!running) return null;
            var index = items.FindIndex(j => j.Status == "queued");
            if (index < 0) { running = false; return null; }
            items[index] = items[index] with { Status = "processing", Stage = "Extracting frames", Error = null };
            Persist(); return (items[index], settings);
        }
    }
    public void Retry(string id) {
        lock (gate) {
            var job = Get(id);
            if (job.Status is not ("failed" or "cancelled")) throw new UserError("Only failed or cancelled videos can be retried.");
            Update(id, j => j with { Status = "queued", Stage = "Queued", Error = null });
        }
    }
    public void Remove(string id) {
        lock (gate) {
            var job = Get(id);
            if (job.Status == "processing") throw new UserError("Cancel the video before removing it.", 409);
            // Only the job's own folder goes: a linked video is never copied here, so the
            // user's original file is untouched. Refreshing the folder re-imports it.
            items.Remove(job); Persist(); Directory.Delete(Folder(id), true);
        }
    }
}
