using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace MeetingEvidence;

public static class SelfTests
{
    public static async Task Run(Corpus corpus, EvidenceSearch search)
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
            passed++;
        }
        Check(corpus.Status.Count == 4 && corpus.Status.All(s => s.Status == "indexed"), "All four real PDFs indexed");
        Check(corpus.Status.Sum(s => s.Pages) > 100, "Corpus includes over 100 PDF pages");
        Check(corpus.Interventions.Count > 40, "Speaker parser recognizes substantive corpus");
        var plastic = search.Search(new("Which delegations mentioned plastic pollution?"));
        Check(plastic.Evidence.Count > 3, "Plastic pollution finds multiple interventions");
        Check(plastic.SearchTerms.Contains("plastic") && !plastic.SearchTerms.Contains("delegations"), "Question boilerplate excluded");
        Check(search.Search(new("What did Samoa say?", "Samoa")).Evidence.All(e => e.Delegation == "Samoa"), "Delegation filter is exact");
        var samoa = search.Search(new("What did Samoa say about oceans?"));
        Check(samoa.Evidence.Count > 0 && samoa.Evidence.All(e => e.Delegation == "Samoa"), "Delegation recognized in natural question");
        Check(corpus.Interventions.Any(i => i.Delegation == "Samoa" && i.RepresentedGroup != null &&
            i.RepresentedGroup.Contains("Alliance of Small Island States")), "Samoa group attribution retained");
        Check(search.Search(new("plastic pollution", Year: 2024)).Evidence.All(e => e.Date.StartsWith("2024")), "Year filter");
        Check(search.Search(new("plastic pollution", Year: 1999)).Evidence.Count == 0, "Out-of-corpus year has no fabricated evidence");
        Check(search.Search(new("xylophonemoonbase")).Evidence.Count == 0, "Unrelated topic abstains");
        Check(search.Search(new("Which States were never elected to the Human Rights Council?")).Evidence.Count == 0, "Unsupported historical negative refused");
        Check(search.Search(new("List oceans statements", GroupsOnly: true)).Evidence.All(e => e.RepresentedGroup != null), "Groups-only filter");
        Check(plastic.Evidence.All(e => !e.Delegation.Contains("President")), "Procedural speakers excluded by default");
        foreach (var e in plastic.Evidence)
        {
            var original = corpus.Interventions.Single(i => i.Id == e.Id).Segments.Single(s => s.Page == e.Page).Text;
            Check(original.Contains(e.Excerpt.Trim('…')), $"Excerpt is an exact source substring: {e.Id}");
        }
        var doc = corpus.Documents[0];
        var synthetic = CorpusLoader.Parse(doc,
        [new(1,"Agenda item 113"), new(1,"Mr. Before (Country A): excluded"),
         new(1,"Agenda item 75"), new(1,"Mr. Example (Country B) (spoke in"), new(1,"French): plastic pollution matters."),
         new(2,"This statement continues on another page."), new(2,"Agenda item 14"), new(2,"Mr. After (Country C): excluded")]);
        Check(synthetic.Count == 1 && synthetic[0].Delegation == "Country B", "Agenda boundaries and wrapped speaker label");
        Check(synthetic[0].Segments.Count == 2 && synthetic[0].PageEnd == 2, "Cross-page attribution preserved");
        var regression = CorpusLoader.Parse(doc,
        [new(1,"Agenda item 75"), new(1,"Mr. Example (Country B): A discussion of the"),
         new(1,"agenda item. Emphasis should remain on oceans."), new(2,"Mr. Next (Country C): More ocean financing.")]);
        Check(regression.Count == 2, "Agenda references inside speeches do not terminate indexing");
        Check(corpus.Interventions.Any(i => i.Delegation == "Brazil" && i.Date.StartsWith("2024")), "Late-document speaker retained");
        Check(!corpus.Interventions.Where(i => i.Delegation == "Singapore").Any(i => i.RepresentedGroup?.StartsWith("my colleague") == true), "Introducing a colleague is not a group statement");
        Check(!corpus.Interventions.SelectMany(i => i.Segments).Any(s => s.Text.Contains("This record contains the text of speeches")), "First-page publication notices excluded");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Generation:Enabled"] = "true", ["Generation:BaseUrl"] = "http://localhost:11434", ["Generation:Model"] = "test"
        }).Build();
        var firstId = plastic.Evidence[0].Id;
        static string ModelResponse(string id) => System.Text.Json.JsonSerializer.Serialize(new { message = new { content =
            System.Text.Json.JsonSerializer.Serialize(new { claims = new[] { new { text = "An evidence-based answer.", evidenceIds = new[] { id } } } }) } });
        var mock = new StubHandler(ModelResponse(firstId));
        var generated = await new AnswerGenerator(config, new HttpClient(mock)).Generate(new("plastic pollution", Generate:true), plastic, default);
        Check(generated.Mode.StartsWith("Local RAG"), "Ollama adapter accepts cited model answer (stub transport)");
        var invalid = new StubHandler(ModelResponse("invented"));
        var rejected = await new AnswerGenerator(config, new HttpClient(invalid)).Generate(new("plastic pollution", Generate:true), plastic, default);
        Check(rejected.Mode == "Evidence search" && rejected.GenerationWarning != null, "Invented model citations rejected");
        var unavailable = new StubHandler("{}", HttpStatusCode.ServiceUnavailable);
        var fallback = await new AnswerGenerator(config, new HttpClient(unavailable)).Generate(new("plastic pollution", Generate:true), plastic, default);
        Check(fallback.Evidence.Count == plastic.Evidence.Count && fallback.Mode == "Evidence search", "Unavailable model preserves working evidence");
        Console.WriteLine($"SUCCESS: {passed} checks passed. Live Ollama inference is not exercised by these tests.");
    }

    private sealed class StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
