using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("CCTV_URL") ?? "http://127.0.0.1:8766");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = VideoTools.MaxBytes);
builder.Services.AddControllersWithViews().AddRazorOptions(options => {
    options.ViewLocationFormats.Clear();
    options.ViewLocationFormats.Add("/Web/Views/{1}/{0}.cshtml");
    options.ViewLocationFormats.Add("/Web/Views/Shared/{0}.cshtml");
});
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
app.UseStaticFiles();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

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
