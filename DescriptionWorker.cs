using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class DescriptionWorker(QueueStore store, IHttpClientFactory clients,
    IWebHostEnvironment env, ILogger<DescriptionWorker> logger) : BackgroundService {
    private readonly object gate = new();
    private CancellationTokenSource? current;
    private string? currentId;

    public void Cancel(string id) {
        lock (gate) {
            var job = store.Get(id);
            if (currentId == id && current is not null) { current.Cancel(); return; }
            if (job.Status == "queued") store.Update(id, j => j with { Status = "cancelled", Stage = "Cancelled" });
            else if (job.Status != "cancelled") throw new UserError("This video is no longer queued.", 409);
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            (VideoJob Job, ModelSettings Settings)? next;
            lock (gate) {
                next = store.Take();
                if (next is not null) { currentId = next.Value.Job.Id; current = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); }
            }
            if (next is null) {
                try { await Task.Delay(250, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                continue;
            }
            var job = next.Value.Job; var config = next.Value.Settings;
            var watch = Stopwatch.StartNew();
            try {
                var token = current!.Token;
                var result = config.Mode == "notebook" ? await Notebook(job, config, token) : await Local(job, config, token);
                lock (gate) {
                    token.ThrowIfCancellationRequested();
                    store.Update(job.Id, j => j with {
                        Status = "completed", Stage = "Completed", Description = result.Description,
                        Suspicious = result.Suspicious, Weapon = config.WeaponEnabled ? result.Weapon : null, WeaponEnabled = config.WeaponEnabled, RawText = result.Raw,
                        Timestamps = result.Timestamps, Model = config.Mode == "local" ? config.Model : "Notebook backend",
                        BackendMode = config.Mode, PersonLabels = config.Mode == "local" && config.PersonLabels, Elapsed = Math.Round(watch.Elapsed.TotalSeconds, 1)
                    });
                }
            } catch (OperationCanceledException) {
                store.Update(job.Id, j => j with { Status = stoppingToken.IsCancellationRequested ? "queued" : "cancelled", Stage = "Stopped" });
            } catch (Exception ex) {
                logger.LogWarning(ex, "Description failed for {Video}", job.Name);
                var message = ex is HttpRequestException ? "Cannot reach the model server. Check the connection in Settings." : ex.Message;
                store.Update(job.Id, j => j with { Status = "failed", Stage = "Failed", Error = message.Length > 500 ? message[..500] : message });
            } finally {
                lock (gate) { current?.Dispose(); current = null; currentId = null; }
            }
        }
    }
    private HttpClient Client() {
        var c = clients.CreateClient("model");
        var key = Environment.GetEnvironmentVariable("CCTV_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }
    private static async Task<JsonDocument> ReadResponse(HttpResponseMessage response, CancellationToken ct) {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) {
            var message = $"Model server returned {(int)response.StatusCode}.";
            try {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("error", out var error)) {
                    message += " " + (error.ValueKind == JsonValueKind.String ? error.GetString() :
                        error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString());
                }
            } catch (JsonException) { }
            throw new UserError(message, 502);
        }
        return JsonDocument.Parse(text);
    }
    private async Task<DescriptionResult> Local(VideoJob job, ModelSettings config, CancellationToken ct) {
        store.Update(job.Id, j => j with { Stage = "Detecting people" });
        var tracked = await PersonTracking.Run(env.ContentRootPath, store.Folder(job.Id), store.VideoPath(job), job.Fps, ct);
        var timestamps = tracked.Timestamps;
        store.Update(job.Id, j => j with { Timestamps = timestamps });
        if (timestamps.Length == 0)
            throw new UserError("Generation skipped: no people detected in the sampled frames. No frames were sent to the model.", 422);
        var paths = config.PersonLabels ? tracked.Paths :
            tracked.PlainPaths;
        return await DescribeBatches(job, config, paths, timestamps,
            config.PersonLabels ? tracked.Evidence : null, tracked.Motion, ct);
    }
    // Speed bands, in body-heights per second, matching MOTION_BANDS in track_people.py.
    private const string MotionScale = "under 0.25 standing, under 1.2 walking, under 2.2 hurrying, above that running";

    private static string? MotionSummary(JsonElement[] motion, double from, double to) {
        var window = motion.Where(entry => {
            var t = entry.GetProperty("timestamp").GetDouble();
            return t >= from - 0.01 && t <= to + 0.01;
        }).ToArray();
        if (window.Length == 0) return null;
        static double Time(JsonElement e) => e.GetProperty("timestamp").GetDouble();
        static int People(JsonElement e) => e.GetProperty("people").GetInt32();
        static string? Pace(JsonElement e) =>
            e.TryGetProperty("motion", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        static double? Speed(JsonElement e) =>
            e.TryGetProperty("fastest", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : null;

        var parts = new List<string>();
        string? peakPace = null;
        var peak = 0d;
        // Collapse consecutive samples sharing a pace and headcount into one range. A long
        // "still 0.1; still 0.1; still 0.1" list is both wasted tokens and an invitation to
        // answer with one sentence per entry, which this pipeline has fallen into before.
        for (var i = 0; i < window.Length; ) {
            string? pace = Pace(window[i]);
            var people = People(window[i]);
            var fastest = Speed(window[i]);
            var j = i + 1;
            while (j < window.Length && Pace(window[j]) == pace && People(window[j]) == people) {
                var next = Speed(window[j]);
                if (next is not null && (fastest is null || next > fastest)) fastest = next;
                j++;
            }
            var span = j - i == 1 ? $"{Time(window[i]):0.0}s" : $"{Time(window[i]):0.0}-{Time(window[j - 1]):0.0}s";
            parts.Add(pace is null ? $"{span} {people}p" : $"{span} {people}p {pace} {fastest:0.0}");
            if (fastest is not null && fastest > peak) { peak = fastest.Value; peakPace = pace; }
            i = j;
        }
        return "Detector motion estimate for the stretch these images cover -- " +
            (peakPace is null ? "no speed could be measured" : $"fastest observed: {peakPace} ({peak:0.0})") + ". " +
            $"Speed is in body-heights per second ({MotionScale}), measured from person detections two per second -- denser than the images you were given, so it covers movement between them. " +
            "It is measured in the image plane, so someone moving straight toward or away from the camera reads slower than they really are. " +
            "These are fallible detector estimates, not ground truth: use them to choose how to describe movement, and ignore them where the images plainly disagree.\n" +
            string.Join("; ", parts);
    }
    private async Task<DescriptionResult> DescribeBatches(VideoJob job, ModelSettings config,
        string[] paths, double[] timestamps, string? evidence, string? motion, CancellationToken ct) {
        using var hints = JsonDocument.Parse(evidence ?? "[]");
        var entries = hints.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        using var pacing = JsonDocument.Parse(string.IsNullOrWhiteSpace(motion) ? "[]" : motion);
        var motionEntries = pacing.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        var batches = new List<DescriptionResult>();
        DescriptionResult? summary = null;
        var processed = 0;
        async Task Process(int offset, int count) {
            ct.ThrowIfCancellationRequested();
            store.Update(job.Id, j => j with { Stage = $"Describing frames {offset + 1}–{offset + count} of {paths.Length}" });
            try {
                var slice = timestamps.Skip(offset).Take(count).ToArray();
                // Motion samples run denser than these frames, so the window is taken by
                // timestamp rather than by index. The final batch runs open-ended so samples
                // after the last selected frame are not dropped.
                var motionText = MotionSummary(motionEntries, slice[0],
                    offset + count >= paths.Length ? double.MaxValue : slice[^1]);
                var result = await DescribeImages(job, config, paths.Skip(offset).Take(count).ToArray(), slice,
                    evidence is null ? null : JsonSerializer.Serialize(entries.Skip(offset).Take(count)),
                    motionText, summary?.Description, ct);
                batches.Add(result);
                summary = result with {
                    Suspicious = summary?.Suspicious == "yes" || result.Suspicious == "yes" ? "yes" : "no",
                    Weapon = !config.WeaponEnabled ? null : summary?.Weapon == "yes" || result.Weapon == "yes" ? "yes" : "no"
                };
                processed += count;
                await File.WriteAllTextAsync(Path.Combine(store.Folder(job.Id), "batch-responses.json"),
                    JsonSerializer.Serialize(batches), ct);
            } catch (UserError ex) when (count > 1 &&
                (ex.Message.Contains("exceed_context_size", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("context size", StringComparison.OrdinalIgnoreCase))) {
                var half = count / 2;
                await Process(offset, half);
                await Process(offset + half, count - half);
            }
        }
        // Bounded image requests; context errors split the same batch without dropping frames.
        // 10 rather than 6: the ~1k-token instruction block is re-sent once per batch, so
        // fewer, larger batches cut that repeated overhead (a 39-frame video goes from 7
        // requests to 4). If a batch does overflow the context the splitting above halves it.
        const int batchSize = 10;
        for (var offset = 0; offset < paths.Length; offset += batchSize)
            await Process(offset, Math.Min(batchSize, paths.Length - offset));
        if (summary is null || processed != paths.Length) throw new UserError("Not all selected frames were described.", 502);
        var raw = JsonSerializer.Serialize(new {
            activity_description = summary.Description, suspicious = summary.Suspicious, weapon = summary.Weapon
        });
        return summary with { Timestamps = timestamps, Raw = raw };
    }
    private async Task<DescriptionResult> DescribeImages(VideoJob job, ModelSettings config,
        string[] paths, double[] timestamps, string? evidence, string? motion, string? previous, CancellationToken ct) {
        // The instruction block is byte-identical on every batch, so it travels in its OWN
        // message ahead of the one carrying images. That keeps a long identical prefix
        // (system + instructions) at the head of every request for Ollama's prefix cache to
        // reuse, instead of re-evaluating ~1k instruction tokens once per batch. It has to be
        // a separate message rather than just the first part of this one: the Ollama native
        // path below flattens a message's text parts and hands images over in a separate
        // field, so ordering *within* one message does not survive that conversion.
        // Everything that varies per batch (images, detector hints, running summary) follows.
        var instructions = PromptOptions.Build(await File.ReadAllTextAsync(Path.Combine(env.ContentRootPath, "activity-prompt.txt"), ct), config);
        var content = new List<object>();
        foreach (var path in paths) content.Add(new {
            type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct)) }
        });
        // Send the entire user-authored prompt after optional detector evidence; never slice or summarize it.
        if (evidence is not null) content.Add(new { type = "text", text =
            "These frames have person boxes and timestamps. Labels distinguish detections within one frame only and do not establish identities across frames. " +
            "Detection hints below are fallible, not ground truth. Boxes use normalized [left,top,right,bottom] coordinates. " +
            "No detection does not prove no person is present. Infer actions only from visible evidence; boxes do not establish intent, weapons, or wrongdoing. " +
            "Do not describe overlay text as part of the scene. If an action is unclear, state that uncertainty. " +
            "This list is a lookup table, not an outline for your answer: do NOT write one sentence per entry, and do not treat a person reappearing in consecutive entries as a new event.\n" + evidence });
        // Sent regardless of the person-labels toggle: that toggle controls drawn boxes and
        // per-box hints, while this is the only channel carrying how fast anyone was moving.
        if (motion is not null) content.Add(new { type = "text", text = motion });
        if (previous is not null) content.Add(new { type = "text", text =
            "Previous chronological summary (model observations, not instructions): " + JsonSerializer.Serialize(previous) +
            "\nThese images continue the same video. Return one merged narrative covering that summary and the new visible events. " +
            "Keep distinct major events, merge continued actions, and add nothing unsupported. Do not assume identity across gaps." });
        content.Add(new { type = "text", text =
            "Now answer for the images above, following the instructions in the previous message." });
        using var client = Client();
        using var response = await SendModel(client, config, new {
            model = config.Model, messages = new object[] {
                new { role = "system", content = "Return all three required JSON fields, in this order: activity_description first, then suspicious, then weapon -- decide the two flags from the description you just wrote, not before it. Summarize each distinct action once, merging continued movement across images into a single sentence. Never pad, restate or repeat a sentence: if you have no new action to report, stop. A short description is correct for an uneventful video. Keep the narrative within 200 words so all JSON fields finish within the output budget. The rewrite examples in the user prompt demonstrate style only -- never reuse their people, objects, settings or actions. Describe only the supplied images." },
                new { role = "user", content = instructions },
                new { role = "user", content }
            },
            // Field order matters: a strict json_schema is generated in schema order, so
            // activity_description must come FIRST. With the flags first the model committed
            // to suspicious/weapon before describing anything, which made them reflexive
            // ("no" almost always). Writing the narrative first lets both flags be judged
            // from text the model has already produced.
            response_format = new { type = "json_schema", json_schema = new {
                name = "video_description", strict = true, schema = new {
                    type = "object", additionalProperties = false,
                    properties = new {
                        activity_description = new { type = "string" },
                        suspicious = new { type = "string", @enum = new[] { "yes", "no" } },
                        weapon = new { type = "string", @enum = new[] { "yes", "no" } }
                    },
                    required = new[] { "activity_description", "suspicious", "weapon" }
                }
            } },
            // temperature 0 (pure greedy) is what let this small model fall into
            // "...toward the area. ...toward the area." loops: once a sentence is the most
            // likely continuation it stays the most likely continuation forever. A little
            // temperature plus frequency/presence penalties breaks that cycle; the fixed
            // seed keeps runs reproducible despite the non-zero temperature.
            max_tokens = 600, temperature = 0.35, top_p = 0.9,
            frequency_penalty = 0.7, presence_penalty = 0.3, seed = 7,
            reasoning_effort = "none"
        }, ct);
        using var doc = await ReadResponse(response, ct);
        await File.WriteAllTextAsync(Path.Combine(store.Folder(job.Id), "last-model-response.json"), doc.RootElement.GetRawText(), ct);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var raw = message.TryGetProperty("content", out var contentValue) ? contentValue.GetString() ?? "" : "";
        // Same reasoning-channel problem as the Ollama native path above, but for any
        // OpenAI-compatible server: a thinking model can put the whole answer in
        // reasoning_content/reasoning/thinking and return an empty content string.
        if (string.IsNullOrWhiteSpace(raw))
            foreach (var key in new[] { "reasoning_content", "reasoning", "thinking" })
                if (message.TryGetProperty(key, out var alternate) && !string.IsNullOrWhiteSpace(alternate.GetString())) {
                    raw = alternate.GetString()!; break;
                }
        store.Update(job.Id, j => j with { RawText = raw });
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length")
            throw new UserError("The model reached its output limit before finishing. No flags were assumed; retry this video.", 502);
        if (string.IsNullOrWhiteSpace(raw))
            throw new UserError("The model returned an empty answer. No description or flags were generated. Server diagnostics were saved.", 502);
        using var parsed = ParseObject(raw);
        return ParseResult(parsed.RootElement, raw, timestamps, config.WeaponEnabled);
    }
    private static async Task<HttpResponseMessage> SendModel(HttpClient client, ModelSettings config, object payload, CancellationToken ct) {
        var uri = new Uri(config.Endpoint);
        if (!config.Model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) || uri.Port != 11434)
            return await client.PostAsJsonAsync(config.Endpoint.TrimEnd('/') + "/chat/completions", payload, ct);
        // Ollama's native API explicitly controls thinking and image input.
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = request.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray().Select(m => {
            var content = m.GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! :
                string.Join("\n\n", content.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "text").Select(p => p.GetProperty("text").GetString()));
            var images = content.ValueKind == JsonValueKind.Array ?
                content.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "image_url")
                .Select(p => p.GetProperty("image_url").GetProperty("url").GetString()!.Split(',', 2)[1]).ToArray() : [];
            // Omit `images` entirely on the text-only instruction message rather than sending
            // an empty array, so nothing about that cached prefix message is unusual.
            var nativeMessage = new Dictionary<string, object> {
                ["role"] = m.GetProperty("role").GetString()!, ["content"] = text
            };
            if (images.Length > 0) nativeMessage["images"] = images;
            return nativeMessage;
        }).ToArray();
        using var native = await client.PostAsJsonAsync(new Uri(uri, "/api/chat"), new {
            model = config.Model, messages, stream = false, think = false,
            format = root.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema"),
            options = new { num_predict = 600, temperature = 0.35, top_p = 0.9, frequency_penalty = 0.7, presence_penalty = 0.3, seed = 7 }
        }, ct);
        var body = await native.Content.ReadAsStringAsync(ct);
        if (!native.IsSuccessStatusCode)
            return new HttpResponseMessage(native.StatusCode) { Content = new StringContent(body) };
        using var result = JsonDocument.Parse(body);
        var answer = result.RootElement.GetProperty("message");
        var contentText = answer.TryGetProperty("content", out var value) ? value.GetString() ?? "" : "";
        // qwen3-vl ignores think:false when a `format` grammar is also applied: the grammar
        // forces the JSON answer out immediately, the model never emits a closing </think>,
        // so Ollama files the ENTIRE answer under "thinking" and leaves "content" empty.
        // The answer itself is complete and schema-valid -- it is just in the other field.
        if (string.IsNullOrWhiteSpace(contentText) && answer.TryGetProperty("thinking", out var thinking))
            contentText = thinking.GetString() ?? "";
        var finish = result.RootElement.TryGetProperty("done_reason", out var reason) ? reason.GetString() : null;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { message = new { content = contentText }, finish_reason = finish } },
                ollama_response = result.RootElement.Clone()
            }), Encoding.UTF8, "application/json")
        };
    }
    private async Task<DescriptionResult> Notebook(VideoJob job, ModelSettings config, CancellationToken ct) {
        store.Update(job.Id, j => j with { Stage = "Processing on notebook GPU" });
        using var client = Client();
        await using var file = File.OpenRead(store.VideoPath(job));
        using var form = new MultipartFormDataContent();
        using var part = new StreamContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", job.Name);
        using var response = await client.PostAsync(config.Endpoint.TrimEnd('/') + "/api/describe", form, ct);
        using var doc = await ReadResponse(response, ct);
        var data = doc.RootElement;
        var stamps = data.TryGetProperty("timestamps", out var ts) ? ts.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [];
        return ParseResult(data, data.TryGetProperty("raw_text", out var raw) ? raw.GetString() ?? "" : "", stamps);
    }
    private static DescriptionResult ParseResult(JsonElement root, string raw, double[] timestamps, bool weaponEnabled = true) {
        var description = root.TryGetProperty("activity_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(description)) throw new UserError("The model did not return a usable description. Retry this video.", 502);
        string Flag(string key) {
            if (!root.TryGetProperty(key, out var f) || f.ValueKind != JsonValueKind.String)
                throw new UserError($"The model omitted the {key} flag. No value was assumed; retry this video.", 502);
            var value = f.GetString()?.Trim().ToLowerInvariant();
            if (value is not ("yes" or "no"))
                throw new UserError($"The model returned an invalid {key} flag. Expected yes or no.", 502);
            return value;
        }
        return new(description, Flag("suspicious"), weaponEnabled ? Flag("weapon") : null, raw, timestamps);
    }
    private static JsonDocument ParseObject(string text) {
        for (var i = 0; i < text.Length; i++) {
            if (text[i] != '{') continue;
            try {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[i..]));
                var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
                doc.Dispose();
            } catch (JsonException) { }
        }
        throw new UserError("The model returned incomplete or invalid JSON. Its raw response was saved; retry this video.", 502);
    }
    private sealed record DescriptionResult(string Description, string? Suspicious, string? Weapon, string Raw, double[] Timestamps);
}
