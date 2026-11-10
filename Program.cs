using System.Text;
using MeetingEvidence;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
builder.Services.AddHttpClient<AnswerGenerator>(client => client.Timeout = Timeout.InfiniteTimeSpan);
var app = builder.Build();
var corpus = await CorpusLoader.Load(app.Environment.ContentRootPath, args.Contains("--rebuild") || args.Contains("--ingest"), app.Logger);
var passages = PassageIndex.Build(corpus);
await File.WriteAllTextAsync(Path.Combine(app.Environment.ContentRootPath, "Data", "passages.json"),
    System.Text.Json.JsonSerializer.Serialize(new { version = PassageIndex.Version, passages }, CorpusLoader.Json));
var embeddings = new EmbeddingIndex(builder.Configuration, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, app.Logger);
if (!args.Contains("--self-test"))
    await embeddings.Initialize(app.Environment.ContentRootPath, passages, args.Contains("--rebuild-embeddings"));
var search = new HybridSearch(corpus, passages, embeddings, builder.Configuration);
var research = new ResearchService(corpus, search);
if (args.Contains("--self-test"))
{
    await SelfTests.Run(corpus, search);
    await HybridTests.Run(corpus, search, embeddings);
    await ResearchTests.Run(corpus, search, research);
    return;
}
if (args.Contains("--evaluate"))
{
    await RetrievalEvaluation.Run(app.Environment.ContentRootPath, search, embeddings);
    return;
}
if (args.Contains("--ingest"))
{
    Console.WriteLine($"Indexed {corpus.Interventions.Count} interventions in {corpus.Status.Count(s => s.Status == "indexed")} documents.");
    Environment.ExitCode = corpus.Status.Any(s => s.Status != "indexed") ? 1 : 0;
    return;
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/corpus", (AnswerGenerator generator) => Results.Ok(new
{
    coverage = search.Coverage, corpus.BuiltAt,
    documents = corpus.Documents.Select(d => new { document = d, ingestion = corpus.Status.Single(s => s.DocumentId == d.Id) }),
    interventions = corpus.Interventions.Count,
    delegations = corpus.Interventions.Where(i => !i.IsProcedural).Select(i => i.Delegation).Distinct().Order().ToArray(),
    generation = new { enabled = generator.Enabled, model = generator.Model }
    , retrieval = new { passages = passages.Count, ready = embeddings.Ready, model = embeddings.Model, warning = embeddings.Warning }
}));
app.MapGet("/api/documents/{id}/pdf", (string id) =>
{
    var doc = corpus.Documents.FirstOrDefault(d => d.Id == id);
    if (doc == null) return Results.NotFound();
    var file = Path.Combine(app.Environment.ContentRootPath, "Data", "raw", doc.FileName);
    return File.Exists(file) ? Results.File(file, "application/pdf", enableRangeProcessing: true) : Results.NotFound();
});
app.MapGet("/api/interventions/{id}", (string id) =>
{
    var intervention = corpus.Interventions.FirstOrDefault(i => i.Id == id);
    return intervention == null ? Results.NotFound() : Results.Ok(intervention);
});
app.MapGet("/api/passages/{id}", (string id) =>
{
    var passage = passages.FirstOrDefault(p => p.Id == id);
    if (passage == null) return Results.NotFound();
    var siblings = passages.Where(p => p.InterventionId == passage.InterventionId).ToList();
    var position = siblings.FindIndex(p => p.Id == id);
    return Results.Ok(new { passage, intervention = corpus.Interventions.Single(i => i.Id == passage.InterventionId),
        context = siblings.Skip(Math.Max(0, position - 1)).Take(position == 0 ? 2 : 3) });
});
app.MapPost("/api/ask", async (QueryRequest request, AnswerGenerator generator, CancellationToken cancellation) =>
{
    if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        return Results.BadRequest(new { error = "Enter a question between 1 and 2,000 characters." });
    if (!new[] { "keyword", "semantic", "hybrid" }.Contains(request.SearchMode))
        return Results.BadRequest(new { error = "Search mode must be keyword, semantic, or hybrid." });
    return Results.Ok(await generator.Generate(request, await search.SearchAsync(request, cancellation), cancellation));
});
app.MapPost("/api/research", async (ResearchRequest input, AnswerGenerator generator, CancellationToken cancellation) =>
{
    if (input.Request == null || string.IsNullOrWhiteSpace(input.Request.Question) || input.Request.Question.Length > 2000 ||
        (input.PreviousRequest != null && (string.IsNullOrWhiteSpace(input.PreviousRequest.Question) || input.PreviousRequest.Question.Length > 2000)))
        return Results.BadRequest(new { error = "Enter a question between 1 and 2,000 characters; previous context must also fit that limit." });
    if (!new[] { "keyword", "semantic", "hybrid" }.Contains(input.Request.SearchMode) || input.CompareYears is { Length: > 2 })
        return Results.BadRequest(new { error = "Choose a supported retrieval mode and at most two comparison years." });
    return Results.Ok(await research.Execute(input, generator, cancellation));
});
app.MapPost("/api/export", async (QueryRequest request, CancellationToken cancellation) =>
{
    if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        return Results.BadRequest(new { error = "Enter a question between 1 and 2,000 characters." });
    if (!new[] { "keyword", "semantic", "hybrid" }.Contains(request.SearchMode))
        return Results.BadRequest(new { error = "Search mode must be keyword, semantic, or hybrid." });
    var result = await search.SearchAsync(request, cancellation, int.MaxValue);
    static string Csv(string? value)
    {
        value ??= "";
        // Prevent spreadsheet applications from interpreting source text as formulas.
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
            value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    var rows = new StringBuilder("Delegation,Speaker,Capacity,Represented group,Date,Document,PDF page,Excerpt,Official source,Corpus coverage,Passage ID,Retrieval mode,Scope\r\n");
    foreach (var e in result.Evidence)
        rows.AppendLine(string.Join(",", new[] { e.Delegation, e.Speaker, e.Capacity, e.RepresentedGroup,
            e.Date, e.Symbol, e.Page.ToString(), e.Excerpt, e.OfficialPdfUrl, result.Coverage, e.Id, result.Retrieval?.Mode, result.Retrieval?.Scope }.Select(Csv)));
    return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(rows.ToString())).ToArray(), "text/csv; charset=utf-8", "meeting-evidence.csv");
});
app.Run();
