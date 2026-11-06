using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeetingEvidence;

public sealed class EmbeddingIndex(IConfiguration config, HttpClient http, ILogger logger)
{
    private Dictionary<string, float[]> _vectors = [];
    public string Model => config["Retrieval:EmbeddingModel"] ?? "nomic-embed-text";
    public string? Warning { get; private set; }
    public bool Ready => _vectors.Count > 0;
    public int Count => _vectors.Count;
    public IReadOnlyDictionary<string, float[]> Vectors => _vectors;
    private record Cache(string Model, string Digest, string ChunkVersion, Dictionary<string, float[]> Vectors);

    public async Task Initialize(string root, List<Passage> passages, bool rebuild = false)
    {
        if (!config.GetValue("Retrieval:Enabled", true)) { Warning = "Semantic retrieval is disabled in configuration."; return; }
        var path = Path.Combine(root, "Data", "embeddings.json");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var tags = await http.GetAsync(Endpoint("/api/tags"), timeout.Token);
            tags.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(timeout.Token));
            var model = json.RootElement.GetProperty("models").EnumerateArray().FirstOrDefault(m =>
                m.GetProperty("name").GetString() is string name && (name == Model || name == Model + ":latest"));
            if (model.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"Embedding model {Model} is not installed.");
            var digest = model.GetProperty("digest").GetString()!;
            Cache? cache = null;
            if (!rebuild && File.Exists(path))
            {
                try { cache = JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(path), CorpusLoader.Json); }
                catch (JsonException) { logger.LogWarning("Embedding cache is invalid; rebuilding it."); }
            }
            var stored = cache is { } && cache.Model == Model && cache.Digest == digest && cache.ChunkVersion == PassageIndex.Version
                ? cache.Vectors : new Dictionary<string, float[]>();
            var missing = passages.Select(p => p.Text).Distinct().Where(t => !stored.ContainsKey(Key(t))).ToList();
            for (var i = 0; i < missing.Count; i += 16)
            {
                var batch = missing.Skip(i).Take(16).ToArray();
                var vectors = await Embed(batch.Select(t => Model.StartsWith("nomic-embed-text") ? "search_document: " + t : t).ToArray(), CancellationToken.None);
                for (var j = 0; j < batch.Length; j++) stored[Key(batch[j])] = vectors[j];
                await Save(path, new(Model, digest, PassageIndex.Version, stored));
                logger.LogInformation("Embedded {Done}/{Total} new passages using {Model}", Math.Min(i + batch.Length, missing.Count), missing.Count, Model);
            }
            var active = passages.ToDictionary(p => p.Id, p => Normalize(stored[Key(p.Text)]));
            if (active.Values.Select(v => v.Length).Distinct().Count() != 1) throw new InvalidDataException("Embedding dimensions differ.");
            _vectors = active;
            logger.LogInformation("Semantic index ready: {Count} passages, {Dimensions} dimensions", Count, _vectors.Values.First().Length);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or InvalidDataException or JsonException or KeyNotFoundException or IOException)
        {
            Warning = "Semantic index unavailable. Start local Ollama, install " + Model + ", then restart the app. Keyword search remains available.";
            logger.LogWarning(ex, "Semantic indexing unavailable; retaining keyword search");
            _vectors = [];
        }
    }

    public async Task<float[]> Query(string question, CancellationToken cancellation)
    {
        var vector = (await Embed([Model.StartsWith("nomic-embed-text") ? "search_query: " + question : question], cancellation, 15))[0];
        if (Ready && vector.Length != _vectors.Values.First().Length) throw new InvalidDataException("Query/index embedding dimensions differ; rebuild embeddings.");
        return vector;
    }

    private async Task<float[][]> Embed(string[] input, CancellationToken cancellation, int timeoutSeconds = 90)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await http.PostAsJsonAsync(Endpoint("/api/embed"),
            new { model = Model, input, truncate = false, keep_alive = "30m" }, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var vectors = json.RootElement.GetProperty("embeddings").EnumerateArray()
            .Select(v => Normalize(v.EnumerateArray().Select(x => x.GetSingle()).ToArray())).ToArray();
        if (vectors.Length != input.Length) throw new InvalidDataException("Embedding count differs from input count.");
        return vectors;
    }

    private Uri Endpoint(string path)
    {
        var configured = config["Generation:BaseUrl"] ?? "http://127.0.0.1:11434";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new InvalidOperationException("Embeddings require a local HTTP Ollama endpoint.");
        return new Uri(uri, path);
    }
    private static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static async Task Save(string path, Cache cache)
    {
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(cache, CorpusLoader.Json));
        File.Move(path + ".tmp", path, true);
    }
    public static float[] Normalize(float[] vector)
    {
        if (vector.Length == 0 || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid embedding values.");
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm < 1e-10) throw new InvalidDataException("Zero embedding vector.");
        return vector.Select(v => (float)(v / norm)).ToArray();
    }
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new InvalidDataException("Embedding dimensions differ.");
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return dot;
    }
}
