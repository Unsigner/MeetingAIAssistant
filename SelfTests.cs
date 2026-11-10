using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
namespace MeetingEvidence;
public static class SelfTests
{
    public static async Task Run(Corpus corpus, HybridSearch search)
    {
        var passed=0;
        void Check(bool ok,string name) {if(!ok)throw new InvalidOperationException("FAIL: "+name);Console.WriteLine("PASS: "+name);passed++;}
        Check(corpus.Documents.Count==10 && corpus.Status.All(s=>s.Status=="indexed"),"All ten official HRC-related PDFs indexed");
        Check(corpus.Status.Sum(s=>s.Pages)==175,"175 original PDF pages retained");
        Check(corpus.Documents.All(d=>d.AgendaItems is {Length:>0}),"Every document declares agenda scope");
        Check(corpus.Interventions.Count>100,"Substantive and procedural interventions indexed");
        Check(!corpus.Interventions.Any(i=>i.DocumentId=="A_76_PV.19" && !i.IsProcedural),"Tribute speeches excluded from election scope");
        Check(corpus.Interventions.Where(i=>i.DocumentId=="A_78_PV.28").All(i=>i.PageEnd<=15),"Other agenda sections excluded");
        Check(corpus.Interventions.Where(i=>i.Capacity.StartsWith("President") && i.Capacity.Contains("Human Rights Council")).All(i=>i.Delegation=="Human Rights Council"),"Council presidents retain institutional capacity");
        Check(corpus.Interventions.Any(i=>i.Delegation=="Iceland" && i.RepresentedGroup!=null),"Group speaking capacity retained");
        Check(!corpus.Interventions.SelectMany(i=>i.Segments).Any(s=>s.Text.Contains("This record contains the text of speeches")),"Publication notices excluded");
        var scoped=await search.SearchAsync(new("What did Brazil say about human rights?","Brazil",2024,SearchMode:"keyword"));
        Check(scoped.Evidence.Count>0 && scoped.Evidence.All(e=>e.Delegation=="Brazil" && e.Date.StartsWith("2024")),"Delegation and year filters");
        var topic=await search.SearchAsync(new("Human Rights Council",SearchMode:"keyword"));
        Check(topic.Evidence.Count>5 && topic.MatchingDelegations>1,"Council topic is not mistaken for a speaker filter");
        var election=await search.SearchAsync(new("election",Year:2024,IncludeProcedural:true,SearchMode:"keyword"));
        Check(election.Evidence.Any(e=>e.Symbol=="A/79/PV.19"),"Known election records are searchable");
        Check((await search.SearchAsync(new("election",Year:2024,SearchMode:"keyword"))).Evidence.All(e=>!e.Delegation.Contains("President")),"Chair exclusion filter");
        var emergency=await search.SearchAsync(new("A/ES-11/PV.10",IncludeProcedural:true,SearchMode:"keyword"));
        Check(emergency.Evidence.Count>0 && emergency.Evidence.All(e=>e.Symbol=="A/ES-11/PV.10"),"Emergency-session symbol filter");
        Check((await search.SearchAsync(new("Which states were never elected to the Human Rights Council?",SearchMode:"keyword"))).Evidence.Count==0,"Unsupported historical negative refused");
        Check((await search.SearchAsync(new("human rights",Year:1999,SearchMode:"keyword"))).Evidence.Count==0,"Out-of-corpus year returns no invented evidence");
        foreach(var d in corpus.Documents) Check(corpus.Interventions.Where(i=>i.DocumentId==d.Id).All(i=>i.PageStart>0 && i.PageEnd<=corpus.Status.Single(s=>s.DocumentId==d.Id).Pages),"Page bounds: "+d.Symbol);
        var doc=corpus.Documents[0] with {AgendaItems=[66]};
        var parsed=CorpusLoader.Parse(doc,[new(1,"Agenda Item 66"),new(1,"Mr. Example (Country A) (spoke in"),new(1,"French): Human rights statement."),new(2,"Continuation."),new(2,"Agenda item 7"),new(2,"Mr. Other (Country B): unrelated")]);
        Check(parsed.Count==1 && parsed[0].Delegation=="Country A" && parsed[0].PageEnd==2,"Configured agenda and wrapped speaker label");
        var role=CorpusLoader.Parse(doc,[new(1,"Agenda item 66"),new(1,"Mr. Example (Country A), President, Human Rights Council: Institutional statement."),new(1,"The Acting President (spoke in French): Thank you.")]);
        Check(role.Count==2 && role[0].Delegation=="Human Rights Council" && role[1].IsProcedural,"Institutional role and translated chair labels");
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Generation:Enabled"]="true",["Generation:BaseUrl"]="http://localhost:11434",["Generation:Model"]="test"}).Build();
        static string Response(string id)=>System.Text.Json.JsonSerializer.Serialize(new{message=new{content=System.Text.Json.JsonSerializer.Serialize(new{claims=new[]{new{text="A source-backed statement.",evidenceIds=new[]{id}}}})}});
        var valid=await new AnswerGenerator(config,new HttpClient(new StubHandler(Response(scoped.Evidence[0].Id)))).Generate(new("human rights",Generate:true),scoped,default);
        Check(valid.Mode.StartsWith("Local RAG"),"Valid model citations accepted");
        var invalid=await new AnswerGenerator(config,new HttpClient(new StubHandler(Response("invented")))).Generate(new("human rights",Generate:true),scoped,default);
        Check(invalid.GenerationWarning!=null && invalid.Mode=="Evidence search","Invented citations rejected");
        var unavailable=await new AnswerGenerator(config,new HttpClient(new StubHandler("{}",HttpStatusCode.ServiceUnavailable))).Generate(new("human rights",Generate:true),scoped,default);
        Check(unavailable.Evidence.Count==scoped.Evidence.Count && unavailable.Mode=="Evidence search","Model outage retains evidence");
        Console.WriteLine($"SUCCESS: {passed} HRC corpus and adapter checks passed.");
    }
    private sealed class StubHandler(string json,HttpStatusCode status=HttpStatusCode.OK):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(json,Encoding.UTF8,"application/json")});
    }
}
