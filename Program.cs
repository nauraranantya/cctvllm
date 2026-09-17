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
app.MapGet("/api/videos/{id}/{asset}", (string id, string asset, QueueStore s) => {
    var job = s.Get(id);
    if (asset is not ("video" or "poster.jpg")) return Results.NotFound();
    var path = Path.Combine(s.Folder(job.Id), asset);
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
public sealed record ModelSettings(string Mode = "local", string Endpoint = "http://127.0.0.1:11434/v1", string Model = "gemma4:e2b-it-qat", bool PersonLabels = true);
public sealed record VideoJob {
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = "queued";
    public string Stage { get; init; } = "Queued";
    public double Duration { get; init; }
    public double Fps { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public string? Description { get; init; }
    public string? Suspicious { get; init; }
    public string? Weapon { get; init; }
    public string? Error { get; init; }
    public string? RawText { get; init; }
    public string? Model { get; init; }
    public string? BackendMode { get; init; }
    public bool PersonLabels { get; init; }
    public double[] Timestamps { get; init; } = [];
    public double? Elapsed { get; init; }
}
public sealed record StoredState(ModelSettings Settings, List<VideoJob> Jobs);
public sealed class QueueStore {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string root;
    private List<VideoJob> items = [];
    private ModelSettings settings = new();
    private bool running;
    public QueueStore(IWebHostEnvironment env) {
        root = Environment.GetEnvironmentVariable("CCTV_DATA_DIR") ?? Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(root, "state.json"))) {
            var saved = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(Path.Combine(root, "state.json")), Json)!;
            settings = saved.Settings;
            items = saved.Jobs.Select(j => j.Status == "processing" ? j with { Status = "queued", Stage = "Queued after restart" } : j).ToList();
        }
    }
    private void Persist() {
        File.WriteAllText(Path.Combine(root, "state.tmp"), JsonSerializer.Serialize(new StoredState(settings, items), Json));
        File.Move(Path.Combine(root, "state.tmp"), Path.Combine(root, "state.json"), true);
    }
    public object Snapshot() { lock (gate) return new { jobs = items.ToArray(), running, settings, frameLimit = VideoTools.MaxFrames }; }
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
            items.Remove(job); Persist(); Directory.Delete(Folder(id), true);
        }
    }
}
