using System.Text.Json;

// JSON-backed development repository. It implements the ADS abstraction so an EF Core
// repository can replace it without changing controllers or processing services.
public sealed class QueueStore : IQueueRepository {
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
