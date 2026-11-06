using System.Text.Json;

namespace MeetingEvidence;

public static class RetrievalEvaluation
{
    private record Target(string InterventionId, int Page, string Contains);
    private record Case(string Name, string Question, bool Negative, Target[] Targets, string[]? TopicAnchors = null);
    public static async Task Run(string root, HybridSearch search, EmbeddingIndex embeddings)
    {
        if (!embeddings.Ready) throw new InvalidOperationException("A live semantic index is required for evaluation; keyword fallback must not be reported as hybrid.");
        var cases = JsonSerializer.Deserialize<List<Case>>(await File.ReadAllTextAsync(Path.Combine(root,"Data","evaluation.json")), CorpusLoader.Json)!;
        var results = new List<object>();
        foreach (var item in cases)
        {
            // Validate all positive labels against the actual source passages before scoring retrieval.
            foreach (var target in item.Targets)
                if (!search.Passages.Any(p => p.InterventionId == target.InterventionId && p.Page == target.Page && p.Text.Contains(target.Contains, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Evaluation label does not match corpus: " + item.Name);
            foreach (var mode in new[] { "keyword", "semantic", "hybrid" })
            {
                var result = await search.SearchAsync(new(item.Question, Year:2023, SearchMode:mode));
                if (mode != "keyword" && result.Retrieval?.Mode != mode) throw new InvalidOperationException("Semantic evaluation fell back to keyword.");
                bool Hit(int k) => item.Targets.Any(t => result.Evidence.Take(k).Any(e => e.InterventionId == t.InterventionId && e.Page == t.Page && e.Excerpt.Contains(t.Contains, StringComparison.OrdinalIgnoreCase)));
                var hit5 = item.Negative ? result.Evidence.Count == 0 : Hit(5);
                var hit10 = item.Negative ? result.Evidence.Count == 0 : Hit(10);
                var topicPhraseAt5 = item.Negative ? result.Evidence.Count == 0 : result.Evidence.Take(5)
                    .Any(e => (item.TopicAnchors ?? []).Any(t => e.Excerpt.Contains(t, StringComparison.OrdinalIgnoreCase)));
                Console.WriteLine($"{item.Name} | {mode} | anchor@5={hit5} anchor@10={hit10} topic-phrase@5={topicPhraseAt5} candidates={result.Evidence.Count}");
                results.Add(new { item.Name, item.Question, mode, hit5, hit10, topicPhraseAt5, count=result.Evidence.Count,
                    top5 = result.Evidence.Take(5).Select(e => new { e.Id,e.InterventionId,e.Symbol,e.Page,e.Excerpt,e.SemanticSimilarity }) });
            }
        }
        var report = new { builtAt=DateTimeOffset.UtcNow, embeddingModel=embeddings.Model,
            note="Small inspected target-passage regression set. Target hit rates are not full relevance recall or precision. Threshold and wording may be tuned on this development set; independent held-out evaluation is still required.", results };
        var path=Path.Combine(root,"Data","retrieval-evaluation.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, CorpusLoader.Json));
        Console.WriteLine("Evaluation saved: " + path);
    }
}
