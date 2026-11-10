using Microsoft.Extensions.Configuration;

namespace MeetingEvidence;

public static class ResearchTests
{
    public static async Task Run(Corpus corpus, HybridSearch search, ResearchService research)
    {
        var passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        var start = new QueryRequest("What did Brazil say about human rights in 2024?", SearchMode:"keyword");
        var first = research.Resolve(new(start));
        Check(first.Request?.Delegation == "Brazil" && first.Request.Year == 2024, "Conversation stores inferred scope");
        var next = research.Resolve(new(start with { Question="same question in 2023" }, first.Request));
        Check(next.Request?.Year == 2023 && !next.Request.Question.Contains("2024"), "Year follow-up replaces old year in topic");
        var before = research.Resolve(new(start with { Question="What about the previous year?" }, first.Request));
        Check(before.Request?.Year == 2023, "Previous year uses prior resolved year");
        var session = research.Resolve(new(start with { Question="What about the previous session?" }, first.Request));
        Check(session.Request?.Session == 78 && session.Request.Year == null, "Session follow-up uses session identity, not a guessed year");
        var groups = research.Resolve(new(start with { Question="show group statements", Year=2024, Delegation="Brazil" },first.Request));
        Check(groups.Request?.GroupsOnly == true && groups.Request.Delegation == "Brazil", "Group shortcut preserves delegation");
        Check(research.Resolve(new(start with {Question="same question in 2023"})).Kind == "clarification", "Follow-up without context asks for a first question");
        Check(research.Resolve(new(new("What about its policies?"),first.Request)).Kind == "clarification", "Unsupported anaphora asks for clarification");
        Check(research.Resolve(new(start with {Delegation="France"})).Kind == "clarification", "Question/filter delegation conflict blocks generation");
        Check(research.Resolve(new(start with {Year=2023})).Kind == "clarification", "Question/filter year conflict blocks generation");
        Check((await search.SearchAsync(start with {Delegation="France"})).Evidence.Count == 0, "Legacy ask/export retrieval also blocks conflicting scope");
        var absent = await search.SearchAsync(new("What did Samoa say about human rights?",SearchMode:"keyword"));
        Check(absent.Evidence.Count == 0, "Named absent speaker does not broaden to other delegations");
        var alias = research.Resolve(new(new("What did Russia say about suspension?","Russian Federation",SearchMode:"keyword")));
        Check(alias.Kind == "answer", "Country alias does not create a false scope conflict");
        Check(research.Resolve(new(new("What did Brazil say about Russia?","Brazil"))).Kind == "answer", "Topic country is not mistaken for the explicitly named speaker");
        var compare = research.Resolve(new(start with {Question="compare with 2023",Year=2024,Delegation="Brazil"},first.Request));
        Check(compare.CompareYears!.SequenceEqual(new[]{2023,2024}) && compare.Request?.Generate == false, "Compare carries topic and bounds two years without LLM summaries");
        Check(research.Resolve(new(start,first.Request,"compare",[2024,2024])).Kind == "clarification", "Duplicate comparison years rejected");
        Check(research.Resolve(new(new("compare with 2020"),new("human rights"))).Kind == "clarification", "One-year comparison without a baseline asks for clarification");
        Check(research.Resolve(new(new("previous session"),new("suspension",Year:2022))).Kind == "clarification", "Mixed regular/emergency sessions require clarification");
        var doc = research.Resolve(new(new("previous session"),new("A/79/PV.19")));
        Check(doc.Request?.Session == 78 && !doc.Request.Question.Contains("A/79/"), "Previous session clears prior document restriction");
        var selected = await search.SearchAsync(new("human rights",SearchMode:"keyword",Session:78));
        Check(selected.Evidence.Count > 0 && selected.Evidence.All(e => corpus.Documents.Single(d => d.Symbol==e.Symbol).Session==78), "Session filters apply to actual source documents");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Generation:Enabled"]="false"}).Build();
        var generator = new AnswerGenerator(config,new HttpClient());
        var reply = await research.Execute(new(start with { Question="compare by year", Delegation="Brazil" },first.Request,"compare",[2023,2024]),generator,default);
        Check(reply.Comparison?.Count == 2 && reply.Comparison[1].Result.Evidence.All(e=>e.Date.StartsWith("2024")), "Comparison evidence stays within each year");
        Check(reply.Comparison!.All(y=>!y.Result.Retrieval!.Exhaustive && y.CorpusRecords==corpus.Documents.Count(d=>d.Date.StartsWith(y.Year.ToString()))), "Comparison discloses unequal corpus coverage");
        var negative = await research.Execute(new(new("Which states were never elected to the Human Rights Council?",SearchMode:"keyword")),generator,default);
        Check(negative.Result?.Evidence.Count == 0, "Conversation does not bypass historical completeness refusal");
        Console.WriteLine($"SUCCESS: {passed} conversational scope and comparison checks passed.");
    }
}
