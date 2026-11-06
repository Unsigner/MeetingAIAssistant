using Microsoft.Extensions.Configuration;
using System.Text;

namespace MeetingEvidence;

public static class HybridTests
{
    public static async Task Run(Corpus corpus, HybridSearch search, EmbeddingIndex embeddings)
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name); passed++;
        }
        Check(search.Passages.Count > corpus.Interventions.Count * 3, "Passage-level corpus built");
        Check(search.Passages.Select(p => p.Id).Distinct().Count() == search.Passages.Count, "Unique passage identifiers");
        Check(search.Passages.All(p => p.Length <= 800 && p.Text == corpus.Interventions.Single(i => i.Id == p.InterventionId)
            .Segments.Single(s => s.Page == p.Page).Text.Substring(p.Start, p.Length)), "Every passage maps exactly to its parent page offsets");
        Check(PassageIndex.Build(corpus).SequenceEqual(search.Passages), "Deterministic passage identifiers and boundaries");
        var filtered = await search.SearchAsync(new("plastic pollution", "Samoa", 2023, SearchMode: "keyword"));
        Check(filtered.Evidence.Count > 0 && filtered.Evidence.All(e => e.Delegation == "Samoa" && e.Date.StartsWith("2023")), "Passage search applies structured filters");
        Check(filtered.Evidence.All(e => e.Id.StartsWith(e.InterventionId + "-p")), "Passage citations preserve parent intervention");
        var multiple = await search.SearchAsync(new("What did Samoa and Tonga say about financing?", SearchMode: "keyword"));
        Check(multiple.Evidence.Count > 0 && multiple.Evidence.All(e => e.Delegation is "Samoa" or "Tonga"), "Multiple named delegations form a union filter");
        var alias = await search.SearchAsync(new("What did Micronesia say about plastic?", SearchMode: "keyword"));
        Check(alias.Evidence.Count > 0 && alias.Evidence.All(e => e.Delegation == "Federated States of Micronesia"), "Delegation alias resolves to printed identity");
        var symbol = await search.SearchAsync(new("A/78/PV.44", SearchMode: "keyword"));
        Check(symbol.Evidence.Count > 0 && symbol.Evidence.All(e => e.Symbol == "A/78/PV.44"), "Exact document symbols form a structured filter");
        var fallback = await search.SearchAsync(new("plastic pollution"));
        Check(fallback.Retrieval?.Mode == "keyword" && fallback.Retrieval.Warning != null, "Offline hybrid request transparently falls back to keyword");
        Check(!fallback.Retrieval!.Exhaustive, "Results never claim exhaustive topic coverage");
        var absent = await search.SearchAsync(new("xylophonemoonbase", SearchMode:"keyword"));
        Check(absent.Evidence.Count == 0, "Unmatched keyword query abstains");
        Check(HybridSearch.Fuse(1, 2) > HybridSearch.Fuse(1, null), "RRF rewards agreement between retrieval methods");
        Check(HybridSearch.Fuse(null, 1) > HybridSearch.Fuse(1, 2, 1.0/3), "Partial keyword overlap cannot overwhelm a stronger semantic match");
        Check(Math.Abs(EmbeddingIndex.Similarity(EmbeddingIndex.Normalize([3,4]), EmbeddingIndex.Normalize([3,4])) - 1) < 1e-6, "Cosine normalization");
        try { EmbeddingIndex.Normalize([float.NaN]); Check(false, "Reject invalid vectors"); }
        catch (InvalidDataException) { Check(true, "Reject invalid vectors"); }
        var testRoot = Path.Combine(Path.GetTempPath(), "MeetingEvidence-HybridTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testRoot, "Data"));
        var handler = new VectorHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Retrieval:EmbeddingModel"] = "test-embed", ["Retrieval:MinSimilarity"] = ".6" }).Build();
        var vectors = new EmbeddingIndex(config, new HttpClient(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var parent = corpus.Interventions[0] with { Id = "test", IsProcedural = false, Delegation = "Example", Segments = [new(1,"Ocean conservation protects biodiversity.")], PageStart=1,PageEnd=1 };
        var smallCorpus = corpus with { Interventions = [parent] };
        var smallPassages = PassageIndex.Build(smallCorpus);
        await vectors.Initialize(testRoot, smallPassages);
        Check(vectors.Ready && handler.EmbeddingCalls == 1, "Vector index embeds initial passages");
        var cached = new EmbeddingIndex(config, new HttpClient(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await cached.Initialize(testRoot, smallPassages);
        Check(cached.Ready && handler.EmbeddingCalls == 1, "Unchanged passage vectors reused from cache");
        var smallSearch = new HybridSearch(smallCorpus, smallPassages, cached, config);
        var lexicalMiss = await smallSearch.SearchAsync(new("aquatic preservation", SearchMode: "keyword"));
        var semanticHit = await smallSearch.SearchAsync(new("aquatic preservation", SearchMode: "hybrid"));
        Check(lexicalMiss.Evidence.Count == 0 && semanticHit.Evidence.Count == 1 && semanticHit.Evidence[0].SemanticRank == 1, "Hybrid can retrieve a passage with no lexical overlap (stub vectors)");
        handler.FailQueries = true;
        var failedQuery = await smallSearch.SearchAsync(new("ocean conservation"));
        Check(failedQuery.Retrieval?.Mode == "keyword" && failedQuery.Retrieval.Warning != null && failedQuery.Evidence.Count == 1, "Embedding service failure retains valid keyword results");
        handler.FailQueries = false; handler.Digest = "changed-model";
        var changed = new EmbeddingIndex(config, new HttpClient(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var before = handler.EmbeddingCalls;
        await changed.Initialize(testRoot, smallPassages);
        Check(changed.Ready && handler.EmbeddingCalls == before + 1, "Changed model digest invalidates cached vectors");
        Console.WriteLine($"SUCCESS: {passed} hybrid/offline checks passed. Use --evaluate for live semantic evaluation.");
    }
    private sealed class VectorHandler : HttpMessageHandler
    {
        public int EmbeddingCalls;
        public bool FailQueries;
        public string Digest = "initial-model";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json;
            if (request.Method == HttpMethod.Get)
                json = System.Text.Json.JsonSerializer.Serialize(new { models = new[] { new { name = "test-embed", digest = Digest } } });
            else
            {
                if (FailQueries) throw new HttpRequestException("Simulated local embedding outage");
                EmbeddingCalls++;
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var count = body.RootElement.GetProperty("input").GetArrayLength();
                json = System.Text.Json.JsonSerializer.Serialize(new { embeddings = Enumerable.Range(0,count).Select(_ => new[] {1f,0f}).ToArray() });
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
