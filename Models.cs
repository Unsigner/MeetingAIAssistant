namespace MeetingEvidence;

public record SourceDocument(string Id, string Symbol, string Date, int Session, string Language,
    string RecordType, string FileName, string RecordUrl, string PdfUrl, string Notes, int[]? AgendaItems = null);
public record SourceLine(int Page, string Text);
public record EvidenceSegment(int Page, string Text);
public record Intervention(string Id, string DocumentId, string Symbol, string Date, string Speaker,
    string Delegation, string Capacity, string? RepresentedGroup, bool IsProcedural,
    int PageStart, int PageEnd, List<EvidenceSegment> Segments);
public record DocumentStatus(string DocumentId, string Sha256, int Pages, int Interventions,
    string Status, string? Error, bool TwoColumns);
public record Corpus(List<SourceDocument> Documents, List<DocumentStatus> Status,
    List<Intervention> Interventions, DateTimeOffset BuiltAt, string ParserVersion);
public record QueryRequest(string Question, string? Delegation = null, int? Year = null,
    bool GroupsOnly = false, bool IncludeProcedural = false, bool Generate = false, string SearchMode = "hybrid", int? Session = null);
public record EvidenceHit(string Id, string Symbol, string Date, string Speaker, string Delegation,
    string Capacity, string? RepresentedGroup, int Page, string Excerpt,
    string LocalPdfUrl, string OfficialPdfUrl, double Score, string InterventionId = "",
    double? SemanticSimilarity = null, int? KeywordRank = null, int? SemanticRank = null);
public record QueryResponse(string Answer, string Mode, string Coverage, string[] SearchTerms,
    int MatchingInterventions, int MatchingDelegations, int ReturnedEvidence,
    bool EvidenceTruncated, List<EvidenceHit> Evidence, string? GenerationWarning,
    RetrievalInfo? Retrieval = null);
public record Passage(string Id, string InterventionId, int Page, int Start, int Length, string Text);
public record RetrievalInfo(string Mode, string Scope, int PassagesInScope, int RetrievedPassages,
    string? EmbeddingModel, string? Warning, bool Exhaustive = false);
public record ResearchRequest(QueryRequest Request, QueryRequest? PreviousRequest = null, string Action = "ask", int[]? CompareYears = null);
public record ResearchPlan(string Kind, string Message, QueryRequest? Request = null, int[]? CompareYears = null);
public record YearEvidence(int Year, int CorpusRecords, QueryResponse Result);
public record ResearchReply(string Kind, string Message, QueryRequest? ResolvedRequest = null,
    QueryResponse? Result = null, List<YearEvidence>? Comparison = null);
