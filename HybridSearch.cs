using System.Text.RegularExpressions;

namespace MeetingEvidence;

public sealed class HybridSearch
{
    private readonly Corpus _corpus;
    private readonly EmbeddingIndex _embeddings;
    private readonly IConfiguration _config;
    private readonly Dictionary<string, Intervention> _parents;
    private readonly Dictionary<string, SourceDocument> _documents;
    private readonly Dictionary<string, Dictionary<string, int>> _tokens;
    private readonly Dictionary<string, int> _documentFrequency;
    private readonly double _averageLength;
    public List<Passage> Passages { get; }
    private static readonly HashSet<string> Stop = new(("a an and are as at be been by can could did do does for from had has have how i in into is it its " +
        "list me of on or our please said say show spoke speak speaking statements statement tell that the their them these they this those to us was were " +
        "what when where which who why will with would about all any countries country delegation delegations discuss discussed mention mentioned " +
        "records record meeting meetings selected available corpus evidence table provide summarize summary find give regarding during between " +
        "only also more details detail thank thanks you your").Split(' '));
    private static readonly Dictionary<string,string[]> Aliases = new()
    {
        ["United States of America"] = ["USA", "United States"], ["United Kingdom"] = ["UK"],
        ["Islamic Republic of Iran"] = ["Iran"], ["Russian Federation"] = ["Russia"],
        ["Federated States of Micronesia"] = ["Micronesia"], ["Bolivarian Republic of Venezuela"] = ["Venezuela"]
    };
    public HybridSearch(Corpus corpus, List<Passage> passages, EmbeddingIndex embeddings, IConfiguration config)
    {
        _corpus = corpus; _embeddings = embeddings; _config = config; Passages = passages;
        _parents = corpus.Interventions.ToDictionary(i => i.Id);
        _documents = corpus.Documents.ToDictionary(d => d.Id);
        _tokens = passages.ToDictionary(p => p.Id, p => EvidenceSearch.Tokens(p.Text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()));
        _documentFrequency = _tokens.Values.SelectMany(b => b.Keys).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        _averageLength = Math.Max(1, _tokens.Count == 0 ? 1 : _tokens.Values.Average(b => b.Values.Sum()));
    }
    public string Coverage => $"Pilot corpus: {_corpus.Status.Count(s => s.Status == "indexed")}/{_corpus.Documents.Count} English General Assembly PVs; " +
        $"{Passages.Count} passages from {_corpus.Interventions.Count} detected interventions. " +
        $"Meeting dates {_corpus.Documents.Min(d => d.Date)} to {_corpus.Documents.Max(d => d.Date)}. Only agenda sections including item 75 are indexed. " +
        "Selected meetings, not continuous historical coverage. Attribution is automatic. Results are retrieval candidates, not an exhaustive list of relevant statements.";
    private record Plan(string[] Terms, string[] Delegations, int[] Years, string[] Symbols, string Scope, string? Refusal);
    private Plan Interpret(QueryRequest request)
    {
        var q = request.Question.Trim();
        var refusal = Regex.IsMatch(q, @"\b(elected|elections?|human rights council|first ever|never served)\b", RegexOptions.IgnoreCase)
            ? "This corpus cannot establish complete election history or historical firsts. Use a verified structured historical dataset." : null;
        var years = request.Year.HasValue ? [request.Year.Value] :
            Regex.Matches(q, @"\b20\d{2}\b").Select(m => int.Parse(m.Value)).Distinct().ToArray();
        var symbols = Regex.Matches(q, @"\bA/\d+/PV\.\d+\b", RegexOptions.IgnoreCase).Select(m => m.Value.ToUpperInvariant()).Distinct().ToArray();
        var delegations = !string.IsNullOrWhiteSpace(request.Delegation) ? [request.Delegation.Trim()] :
            _corpus.Interventions.Where(i => !i.IsProcedural).Select(i => i.Delegation).Distinct().Where(d =>
                new[] { d }.Concat(Aliases.GetValueOrDefault(d) ?? []).Any(name =>
                    Regex.IsMatch(q, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase))).ToArray();
        var nameTerms = delegations.SelectMany(d => new[] { d }.Concat(Aliases.GetValueOrDefault(d) ?? [])).SelectMany(EvidenceSearch.Tokens).ToHashSet();
        var topic = symbols.Aggregate(q, (text, symbol) => text.Replace(symbol, "", StringComparison.OrdinalIgnoreCase));
        var terms = EvidenceSearch.Tokens(topic).Where(t => !Stop.Contains(t) && !nameTerms.Contains(t) && !Regex.IsMatch(t, @"^\d+$")).Distinct().ToList();
        string[] generic = ["ocean", "sea", "marine", "law"];
        if (terms.Any(t => !generic.Contains(t))) terms.RemoveAll(t => generic.Contains(t));
        var scope = $"Delegations: {(delegations.Length == 0 ? "all" : string.Join(", ", delegations))}. " +
            $"Years: {(years.Length == 0 ? "all corpus years" : string.Join(", ", years))}. " +
            (symbols.Length > 0 ? "Documents: " + string.Join(", ", symbols) + ". " : "") +
            (request.GroupsOnly ? "Group statements only. " : "") +
            (request.IncludeProcedural ? "Chair/Secretariat included." : "Chair/Secretariat excluded.");
        return new(terms.ToArray(), delegations, years, symbols, scope, refusal);
    }
    public async Task<QueryResponse> SearchAsync(QueryRequest request, CancellationToken cancellation = default, int limit = 200)
    {
        var plan = Interpret(request);
        if (request.SearchMode == "keyword" || plan.Refusal != null || Passages.Count == 0)
            return Run(request, plan, null, null, limit);
        if (!_embeddings.Ready)
            return Run(request with { SearchMode = "keyword" }, plan, null,
                _embeddings.Warning ?? "Semantic index unavailable; this request used keyword search.", limit);
        try
        {
            var vector = await _embeddings.Query(request.Question, cancellation);
            return Run(request, plan, vector, null, limit);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            return Run(request with { SearchMode = "keyword" }, plan, null, "Query embedding failed. Results below use keyword search only.", limit);
        }
    }
    private QueryResponse Run(QueryRequest request, Plan plan, float[]? vector, string? warning, int limit)
    {
        var scope = Passages.Where(p =>
        {
            var i = _parents[p.InterventionId];
            return (request.IncludeProcedural || !i.IsProcedural) &&
                (plan.Delegations.Length == 0 || plan.Delegations.Contains(i.Delegation, StringComparer.OrdinalIgnoreCase)) &&
                (plan.Years.Length == 0 || plan.Years.Any(y => i.Date.StartsWith(y.ToString()))) &&
                (plan.Symbols.Length == 0 || plan.Symbols.Contains(i.Symbol)) &&
                (!request.GroupsOnly || i.RepresentedGroup != null);
        }).ToList();
        var mode = vector == null ? "keyword" : request.SearchMode;
        var info = new RetrievalInfo(mode, plan.Scope, scope.Count, 0, vector != null ? _embeddings.Model : null, warning);
        QueryResponse Empty(string text) => new(text, "Evidence search", Coverage, plan.Terms, 0, 0, 0, false, [], null, info);
        if (plan.Refusal != null) return Empty(plan.Refusal);
        if (Passages.Count == 0) return Empty("No passages were indexed. Inspect the corpus status and rebuild.");
        if (scope.Count == 0) return Empty("No passages exist within these filters. This does not establish historical absence.");
        if (plan.Terms.Length == 0)
        {
            if (plan.Delegations.Length == 0 && plan.Symbols.Length == 0 && !request.GroupsOnly && !Regex.IsMatch(request.Question, @"\b(speakers?|delegations?|statements?|oceans?)\b", RegexOptions.IgnoreCase))
                return Empty("Enter an oceans topic or select a delegation.");
            return Create(scope.Select(p => (p, Score: 0.0, Similarity: (double?)null, Keyword: (int?)null, Semantic: (int?)null)).ToList(), info with { Mode = "browse" }, plan, limit);
        }
        var lexical = scope.Select(p =>
        {
            var bag = _tokens[p.Id]; double score = 0;
            foreach (var term in plan.Terms)
            {
                if (!bag.TryGetValue(term, out var frequency)) continue;
                var df = _documentFrequency[term];
                var idf = Math.Log(1 + (_tokens.Count - df + .5) / (df + .5));
                score += idf * frequency * 2.2 / (frequency + 1.2 * (.25 + .75 * bag.Values.Sum() / _averageLength));
            }
            return (Passage: p, Score: score);
        }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Passage.Id).ToList();
        var keywordRanks = lexical.Select((x, n) => (x.Passage.Id, Rank: n + 1)).ToDictionary(x => x.Id, x => x.Rank);
        if (mode == "keyword")
            return Create(lexical.Select(x => (x.Passage, x.Score, (double?)null, (int?)keywordRanks[x.Passage.Id], (int?)null)).ToList(), info, plan, limit);
        var minSimilarity = _config.GetValue("Retrieval:MinSimilarity", .60);
        var candidateLimit = Math.Clamp(_config.GetValue("Retrieval:CandidateLimit", 40), 5, 200);
        var semantic = scope.Select(p => (Passage: p, Score: EmbeddingIndex.Similarity(vector!, _embeddings.Vectors[p.Id])))
            .Where(x => x.Score >= minSimilarity).OrderByDescending(x => x.Score).ThenBy(x => x.Passage.Id).Take(candidateLimit).ToList();
        var semanticRanks = semantic.Select((x, n) => (x.Passage.Id, Rank: n + 1)).ToDictionary(x => x.Id, x => x.Rank);
        var similarities = semantic.ToDictionary(x => x.Passage.Id, x => x.Score);
        if (mode == "semantic")
            return Create(semantic.Select(x => (x.Passage, x.Score, (double?)x.Score, (int?)null, (int?)semanticRanks[x.Passage.Id])).ToList(), info, plan, limit);
        var pool = lexical.Take(candidateLimit).Select(x => x.Passage).Concat(semantic.Select(x => x.Passage)).DistinctBy(p => p.Id);
        var fused = pool.Select(p =>
        {
            int? k = keywordRanks.TryGetValue(p.Id, out var kr) && kr <= candidateLimit ? kr : null;
            int? s = semanticRanks.TryGetValue(p.Id, out var sr) ? sr : null;
            var coverage = plan.Terms.Count(t => _tokens[p.Id].ContainsKey(t)) / (double)plan.Terms.Length;
            return (p, Score: Fuse(k, s, coverage), Similarity: similarities.TryGetValue(p.Id, out var sim) ? (double?)sim : null, Keyword: k, Semantic: s);
        }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.p.Id).ToList();
        return Create(fused, info, plan, limit);
    }
    public static double Fuse(int? keywordRank, int? semanticRank, double keywordCoverage = 1) =>
        // Partial lexical matches remain candidates, but only complete topic-term coverage earns a lexical rank bonus.
        (keywordRank.HasValue && keywordCoverage >= 1 ? 1.0 / (60 + keywordRank.Value) : 0) + (semanticRank.HasValue ? 1.0 / (60 + semanticRank.Value) : 0);
    private QueryResponse Create(List<(Passage Passage, double Score, double? Similarity, int? Keyword, int? Semantic)> ranked, RetrievalInfo info, Plan plan, int limit)
    {
        var hits = ranked.Take(limit).Select(x =>
        {
            var i = _parents[x.Passage.InterventionId]; var d = _documents[i.DocumentId];
            return new EvidenceHit(x.Passage.Id, i.Symbol, i.Date, i.Speaker, i.Delegation, i.Capacity, i.RepresentedGroup,
                x.Passage.Page, x.Passage.Text, $"/api/documents/{d.Id}/pdf#page={x.Passage.Page}",
                $"{d.PdfUrl}#page={x.Passage.Page}", Math.Round(x.Score, 6), i.Id, x.Similarity, x.Keyword, x.Semantic);
        }).ToList();
        var interventionCount = ranked.Select(x => x.Passage.InterventionId).Distinct().Count();
        var delegationCount = ranked.Select(x => _parents[x.Passage.InterventionId].Delegation).Distinct().Count();
        var answer = hits.Count == 0 ? "No candidates passed this search. Try different wording, broader filters, or another retrieval mode. This is not proof of historical absence."
            : $"Retrieved {ranked.Count} passage candidates from {interventionCount} interventions and {delegationCount} delegations or organizations. Review the evidence; this is not an exhaustive topic assessment.";
        return new(answer, "Evidence search", Coverage, plan.Terms, interventionCount, delegationCount, hits.Count,
            ranked.Count > limit, hits, null, info with { RetrievedPassages = ranked.Count });
    }
}
