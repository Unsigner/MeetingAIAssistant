using System.Text.RegularExpressions;

namespace MeetingEvidence;

// The demo carries a typed request, not an unbounded LLM chat transcript. No server conversation storage.
public sealed class ResearchService(Corpus corpus, HybridSearch search)
{
    private static readonly Regex YearPattern = new(@"\b20\d{2}\b");
    private static readonly Regex SymbolPattern = new(@"\bA/(?:\d+|ES-\d+)/PV\.\d+\b", RegexOptions.IgnoreCase);
    private static string WithoutTime(string question)
    {
        var topic = Regex.Replace(YearPattern.Replace(SymbolPattern.Replace(question, ""), ""), @"\s+", " ").Trim(' ', '?', '.');
        return topic.Length == 0 ? "Show statements" : topic;
    }
    public ResearchPlan Resolve(ResearchRequest input)
    {
        var current = input.Request;
        var previous = input.PreviousRequest;
        var message = current.Question.Trim();
        var text = message.TrimEnd('?', '.', '!').ToLowerInvariant();
        ResearchPlan Clarify(string reason) => new("clarification", reason);
        if (input.Action is not ("ask" or "groups" or "compare" or "previous-year" or "previous-session"))
            return Clarify("That research action is not supported by this demo.");
        var years = YearPattern.Matches(message).Select(m => int.Parse(m.Value)).Distinct().ToArray();
        var group = input.Action == "groups" || Regex.IsMatch(text, @"^(?:show|only|just)(?: me)?(?: the)? group statements(?: only)?$");
        var compare = input.Action == "compare" || Regex.IsMatch(text, @"^compare (?:by year|(?:with |years? )?20\d{2}(?: (?:and|with|versus|vs) 20\d{2})?)$");
        var previousYear = input.Action == "previous-year" || Regex.IsMatch(text, @"^(?:(?:what|how) about (?:the )?)?previous year$");
        var previousSession = input.Action == "previous-session" || Regex.IsMatch(text, @"^(?:(?:what|how) about (?:the )?)?previous session$");
        var changeYear = Regex.IsMatch(text, @"^(?:(?:what|how) about |same (?:question|topic)(?: in)? |in |only |and )?20\d{2}$");
        var followup = group || compare || previousYear || previousSession || changeYear;
        if (!followup && Regex.IsMatch(text, @"^(?:what|how) about\b|^(?:same question|same topic|compare)\b"))
            return Clarify("This demo supports follow-ups such as ‘same question in 2023’, ‘show group statements’, ‘previous year’, ‘previous session’, and ‘compare 2023 and 2024’. For a different topic, ask a complete question.");
        QueryRequest resolved;
        var explanation = "Research scope resolved from this question and the selected filters.";
        if (followup)
        {
            if (previous == null) return Clarify("Ask a research question first, then refine or compare its evidence.");
            // Current controls are explicit; source topic and prior inference are retained independently.
            resolved = current with { Question = previous.Question };
            explanation = "Continued the previous topic with the current research filters.";
            if (group) resolved = resolved with { GroupsOnly = true };
            if (changeYear || previousYear)
            {
                var baseline = previous.Year;
                if (previousYear && baseline == null) return Clarify("Select a single year or ask about a specific year before requesting the previous year.");
                resolved = resolved with { Question = WithoutTime(previous.Question), Year = changeYear ? years[0] : baseline!.Value - 1, Session = null };
                explanation = "Reused the previous topic and delegation; replaced its year and removed previous document/session restrictions.";
            }
            if (previousSession)
            {
                var matching = corpus.Documents.Where(d => previous.Session.HasValue ? d.Session == previous.Session :
                    previous.Year.HasValue ? d.Date.StartsWith(previous.Year.Value.ToString()) :
                    SymbolPattern.Matches(previous.Question).Any(m => d.Symbol.Equals(m.Value, StringComparison.OrdinalIgnoreCase))).ToList();
                var sessions = matching.Select(d => d.Session).Distinct().ToArray();
                if (sessions.Length != 1 || sessions[0] < 60)
                    return Clarify("The previous General Assembly session is ambiguous here. Start from one regular-session year or document; emergency sessions are a separate series.");
                resolved = resolved with { Question = WithoutTime(previous.Question), Year = null, Session = sessions[0] - 1 };
                explanation = $"Reused the topic for General Assembly session {sessions[0] - 1}. A session is not the same as a calendar year; only available records are searched.";
            }
            if (compare)
            {
                if (input.CompareYears == null && years.Length == 1 && !previous.Year.HasValue)
                    return Clarify("Name both comparison years, or start from a single-year question before asking ‘compare with’ another year.");
                var available = corpus.Documents.Select(d => int.Parse(d.Date[..4])).Distinct().Order().ToArray();
                var selected = input.CompareYears ?? (years.Length == 2 ? years : years.Length == 1 && previous.Year.HasValue
                    ? [previous.Year.Value, years[0]] : available.TakeLast(2).ToArray());
                selected = selected.Distinct().Order().ToArray();
                if (selected.Length != 2 || selected.Any(y => y is < 1900 or > 2100))
                    return Clarify("Choose two different years for the comparison.");
                resolved = resolved with { Question = WithoutTime(previous.Question), Year = null, Session = null, Generate = false };
                if (search.ScopeConflict(resolved) is string conflict) return Clarify(conflict);
                var comparisonNames = search.DetectDelegations(resolved.Question);
                if (string.IsNullOrWhiteSpace(resolved.Delegation) && comparisonNames.Length == 1)
                    resolved = resolved with { Delegation = comparisonNames[0] };
                return new("comparison", "Side-by-side retrieved evidence from two selected years. Different meetings and sample sizes prevent exhaustive counts, trend claims, or conclusions that a delegation changed position. Comparisons use evidence search without generated summaries.", resolved, selected);
            }
        }
        else resolved = current;
        if (search.ScopeConflict(resolved) is string scopeConflict) return Clarify(scopeConflict);
        var named = search.DetectDelegations(resolved.Question);
        if (string.IsNullOrWhiteSpace(resolved.Delegation) && named.Length == 1) resolved = resolved with { Delegation = named[0] };
        var explicitYears = YearPattern.Matches(resolved.Question).Select(m => int.Parse(m.Value)).Distinct().ToArray();
        if (!resolved.Year.HasValue && explicitYears.Length == 1) resolved = resolved with { Year = explicitYears[0] };
        return new("answer", explanation, resolved);
    }

    public async Task<ResearchReply> Execute(ResearchRequest input, AnswerGenerator generator, CancellationToken cancellation)
    {
        var plan = Resolve(input);
        if (plan.Request == null) return new(plan.Kind, plan.Message);
        if (plan.Kind == "comparison")
        {
            var compared = new List<YearEvidence>();
            foreach (var year in plan.CompareYears!)
            {
                var result = await search.SearchAsync(plan.Request with { Year = year, Generate = false }, cancellation);
                compared.Add(new(year, corpus.Documents.Count(d => d.Date.StartsWith(year.ToString())), result));
            }
            return new(plan.Kind, plan.Message, plan.Request, Comparison: compared);
        }
        var found = await search.SearchAsync(plan.Request, cancellation);
        return new(plan.Kind, plan.Message, plan.Request, await generator.Generate(plan.Request, found, cancellation));
    }
}
