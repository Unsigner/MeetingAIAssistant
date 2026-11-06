using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetingEvidence;

public sealed class EvidenceSearch(Corpus corpus)
{
    private static readonly HashSet<string> Stop = new(("a an and are as at be been by can could did do does for from had has have how i in into is it its " +
        "list me of on or our please said say show spoke speak speaking statements statement tell that the their them these they this those to us was were " +
        "what when where which who why will with would about all any countries country delegation delegations discuss discussed mention mentioned " +
        "records record meeting meetings selected available corpus evidence table provide summarize summary find give regarding during between " +
        "only also more details detail thank thanks you your").Split(' '));

    private readonly Dictionary<string, Dictionary<string, int>> _tokens = corpus.Interventions.ToDictionary(i => i.Id,
        i => Tokens(string.Join(" ", i.Segments.Select(s => s.Text))).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()));
    private readonly Dictionary<string, SourceDocument> _documents = corpus.Documents.ToDictionary(d => d.Id);

    public string Coverage => $"Pilot corpus: {corpus.Status.Count(s => s.Status == "indexed")}/{corpus.Documents.Count} English General Assembly PVs indexed; " +
        $"meeting dates {corpus.Documents.Min(d => d.Date)} to {corpus.Documents.Max(d => d.Date)}. Only agenda sections including item 75 are indexed. " +
        "Selected meetings, not continuous historical coverage. Attribution and group detection are automatic; verify source pages. " +
        (corpus.Status.Any(s => s.Status != "indexed") ? "Some documents failed ingestion; consult the corpus list. " : "") +
        "Counts describe lexical matches in this pilot, not all relevant UN statements.";

    public QueryResponse Search(QueryRequest request, int limit = 200)
    {
        if (corpus.Interventions.Count == 0)
            return Empty("No source interventions were successfully indexed. Inspect the corpus status, download the PDFs, and rebuild the index.");
        var question = request.Question.Trim();
        if (Regex.IsMatch(question, @"\b(elect(?:ed|ion|ions)?|human rights council|never|vot(?:e|ed|ing)|first ever)\b", RegexOptions.IgnoreCase))
            return Empty("This pilot indexes oceans statements, not a complete election or voting dataset. It cannot establish this historical or procedural claim.");
        var terms = Tokens(question).Where(t => !Stop.Contains(t) && !Regex.IsMatch(t, @"^\d+$")).Distinct().ToList();
        var year = request.Year;
        var years = Regex.Matches(question, @"\b20\d{2}\b").Select(m => int.Parse(m.Value)).Distinct().ToArray();
        if (year == null && years.Length == 1) year = years[0];
        var delegation = request.Delegation?.Trim();
        // Recognize an explicit country name in a fresh question, without inferring group membership.
        if (string.IsNullOrWhiteSpace(delegation))
        {
            delegation = corpus.Interventions.Where(i => !i.IsProcedural && i.Delegation != "Unresolved")
                .Select(i => i.Delegation).Distinct().OrderByDescending(d => d.Length)
                .FirstOrDefault(d => Regex.IsMatch(question, @"\b" + Regex.Escape(d) + @"\b", RegexOptions.IgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(delegation))
        {
            var nameTerms = Tokens(delegation).ToHashSet();
            terms.RemoveAll(nameTerms.Contains);
        }
        // Common corpus-wide words should not overwhelm a specific topic such as plastic pollution.
        string[] generic = ["ocean", "oceans", "sea", "marine", "law"];
        if (terms.Any(t => !generic.Contains(t))) terms.RemoveAll(t => generic.Contains(t));
        var meaningful = terms.Count > 0;
        var allowBrowse = !string.IsNullOrWhiteSpace(delegation) || request.GroupsOnly ||
            Regex.IsMatch(question, @"\b(oceans?|law of the sea|speakers?|delegations?|statements?)\b", RegexOptions.IgnoreCase);
        if (!meaningful && !allowBrowse)
            return Empty("Enter an oceans topic or delegation, for example ‘Which delegations mentioned plastic pollution?’");
        var candidates = corpus.Interventions.Where(i =>
            (request.IncludeProcedural || !i.IsProcedural) &&
            (year == null || i.Date.StartsWith(year.Value.ToString())) &&
            (string.IsNullOrWhiteSpace(delegation) || i.Delegation.Equals(delegation, StringComparison.OrdinalIgnoreCase)) &&
            (!request.GroupsOnly || i.RepresentedGroup != null));
        var averageLength = Math.Max(1, _tokens.Values.Average(t => t.Values.Sum()));
        var scored = candidates.Select(i =>
        {
            var bag = _tokens[i.Id];
            double score = 0;
            var length = bag.Values.Sum();
            foreach (var term in terms)
            {
                if (!bag.TryGetValue(term, out var frequency)) continue;
                var documentFrequency = _tokens.Values.Count(t => t.ContainsKey(term));
                var idf = Math.Log(1 + (_tokens.Count - documentFrequency + .5) / (documentFrequency + .5));
                score += idf * frequency * 2.2 / (frequency + 1.2 * (.25 + .75 * length / averageLength));
            }
            return (Intervention: i, Score: score);
        }).Where(x => !meaningful || x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Intervention.Id).ToList();
        var selected = scored.Take(limit).Select(x => Hit(x.Intervention, terms, x.Score)).ToList();
        var count = scored.Select(x => x.Intervention.Delegation).Distinct().Count();
        var filters = (year.HasValue ? $" Year: {year}." : "") + (!string.IsNullOrWhiteSpace(delegation) ? $" Delegation: {delegation}." : "");
        var answer = selected.Count == 0
            ? "No matching intervention was found in the selected pilot records. This is not evidence that the topic was never discussed. Try alternative wording or broader filters."
            : $"Matching interventions: {scored.Count}. Distinct delegations or organizations: {count}. Scope: selected pilot records." +
              " The evidence below contains source excerpts, not an AI-generated interpretation." + filters;
        return new(answer, "Evidence search", Coverage, terms.ToArray(), scored.Count, count, selected.Count,
            scored.Count > limit, selected, null);
    }

    private QueryResponse Empty(string answer) => new(answer, "Evidence search", Coverage, [], 0, 0, 0, false, [], null);

    private EvidenceHit Hit(Intervention intervention, List<string> terms, double score)
    {
        var segment = intervention.Segments.OrderByDescending(s => Tokens(s.Text).Count(terms.Contains)).First();
        var text = segment.Text;
        // Select a contiguous window in the source text; never manufacture an extractive quote.
        if (text.Length > 1050)
        {
            var sentences = Regex.Matches(text, @"[^.!?]+[.!?]?");
            var best = sentences.OrderByDescending(s => Tokens(s.Value).Count(terms.Contains)).First();
            var start = Math.Max(0, best.Index - 100);
            if (start > 0)
            {
                var boundary = text.IndexOf(' ', start);
                if (boundary >= 0) start = boundary + 1;
            }
            var end = Math.Min(text.Length, start + 1050);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOf(' ', end - 1);
                if (boundary > start) end = boundary;
            }
            text = (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
        }
        var source = _documents[intervention.DocumentId];
        return new(intervention.Id, intervention.Symbol, intervention.Date, intervention.Speaker, intervention.Delegation,
            intervention.Capacity, intervention.RepresentedGroup, segment.Page, text,
            $"/api/documents/{source.Id}/pdf#page={segment.Page}", $"{source.PdfUrl}#page={segment.Page}", Math.Round(score, 4));
    }

    public static IEnumerable<string> Tokens(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var clean = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        foreach (Match match in Regex.Matches(clean, @"[a-z0-9]+"))
        {
            var token = match.Value;
            yield return token switch
            {
                "financing" or "funding" or "financial" or "funds" or "finance" => "finance",
                "fisheries" or "fishing" or "fishery" => "fishery",
                "pollution" or "polluted" or "polluting" => "pollution",
                "plastics" or "plastic" => "plastic",
                "oceans" => "ocean",
                "rising" or "rise" => "rise",
                "levels" => "level",
                _ => token
            };
        }
    }
}
