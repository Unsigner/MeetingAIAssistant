# Meeting Evidence Assistant

A .NET demo for researching **Human Rights Council elections, membership procedures, and debate** in official General Assembly meeting records. It combines keyword and semantic retrieval, optional local AI answers, source inspection, and CSV export.

![Two-year HRC evidence comparison in the research workspace](Documentation/Research-Workspace.png)

## Download, build, and run

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Ollama](https://ollama.com/download/windows). Clone the repository or extract its ZIP into a writable folder. PDFs, extracted text, corpus, passages, and embeddings are included; there is no prebuilt application or bundled Ollama/model installation.

Start Ollama and download both models once:

```powershell
ollama pull nomic-embed-text
ollama pull qwen2.5:1.5b
```

From the project root:

```powershell
dotnet restore
dotnet build
dotnet run --no-build --no-launch-profile
```

Open http://localhost:5086. Alternatively, double-click `Start.cmd`, which restores, builds, and runs the source. Visual Studio uses its own launch-profile URL; run one app instance per port. Press Ctrl+C to stop the app. Ollama is managed separately through its Windows app or `ollama serve`.

`appsettings.json` enables semantic search and answer generation by default. Optional private overrides belong in ignored `appsettings.Local.json`; an example is included. Both model endpoints use local Ollama at `http://127.0.0.1:11434`. No API keys are required. Initial dependency/model downloads require Internet access. A different installed embedding model digest triggers automatic re-embedding on startup.

## Demo questions

- Which delegations discussed universal periodic review?
- What did Brazil say about human rights in 2024?
- What did Switzerland say about the death penalty?
- What did Russia say about suspension in 2022?
- What election results were recorded in A/79/PV.19?

**Include chair / Secretariat** is checked initially because election rules and announcements appear in the presiding officer's record. They are labelled as institutional/procedural evidence, not statements by the states named in election results. Uncheck it to focus on delegation statements. Council presidents presenting reports retain their institutional capacity rather than being attributed to their country's national position. Filters include delegation, dynamically detected meeting years (2020–2024), group capacity, and retrieval mode.

Known, scoped election proceedings are searchable. The demo still refuses questions using “never,” “first ever,” “all time,” or “ever elected”: ten selected records cannot establish complete historical absence. It does not implement a verified election-history database or comprehensive table of all elected/never-elected states.

## Conversational research workspace

The interface keeps a conversation in the current browser tab. Each answer stores its resolved scope, retrieved evidence, citations, and CSV snapshot. New conversation clears history and scope; refreshing also clears history. There are no accounts, saved conversations, server chat storage, or general-purpose agent memory.

1. Ask a full question, such as “Which delegations discussed universal periodic review?”
2. Refine with “same question in 2023”, “show group statements”, “previous year”, or “previous session”. Unsupported follow-ups ask for clarification instead of guessing.
3. Use “Compare by year” below an answer, with the two years selected in the sidebar, or ask “compare 2023 and 2024”. “Compare with 2023” requires a single-year baseline. Comparisons run two evidence searches with generated summaries off, show each year's corpus size, and make no complete-count, trend, or policy-change claim.
4. Inspect the grouped evidence table. A group label supplements the actual speaking delegation. Expand further passages, inspect context/full interventions, follow citations to their own answer's evidence, or open PDF source pages.
5. Export returned evidence from a specific answer. CSV is generated from that immutable browser snapshot, without rerunning search or applying later filters. It includes the original message, resolved question, source references, and scope; it contains only returned passages, including any response cap. Comparison CSV includes the year for each row.

The sidebar fixes meeting body to General Assembly and record type to PV because the corpus supports no alternatives. Filters and models remain available for the next request; earlier answers keep their own scope. Corpus coverage and all ten indexed records can be opened from the top banner or sidebar.

Explicit delegation/year filters conflicting with a named speaker/year in the question now block retrieval and generation, including legacy ask/export endpoints. Align them and retry. An explicit absent speaker returns no matching evidence rather than broadening to other delegations. Name/intent detection remains rule-based: this is a bounded research conversation, not unrestricted natural-language understanding.

“Previous session” uses actual General Assembly session metadata, not calendar-year subtraction. Start from a single regular-session year or document. Ambiguous scopes and emergency-session contexts request clarification. Changing the year clears earlier document/session restrictions so they do not silently prevent matching records.

## Source corpus

**10 English General Assembly PV records, 175 PDF pages, 151 detected interventions, and 921 passages.** These are General Assembly proceedings concerning the HRC, including elections held by the Assembly; they are not ten Geneva HRC session transcripts. There is no SR corpus in this demo. The selected dates range from 13 October 2020 to 1 November 2024.

| Official symbol | Meeting date | Pages | Indexed subject |
|---|---|---:|---|
| A/75/PV.16 | 2020-10-13 | 14 | HRC election; agenda 119 |
| A/76/PV.19 | 2021-10-14 | 11 | HRC election; agenda 116; unrelated tributes excluded |
| A/ES-11/PV.10 | 2022-04-07 | 25 | Suspension debate; emergency-session agenda 5 |
| A/76/PV.71 | 2022-05-10 | 4 | HRC by-election; agenda 116 |
| A/77/PV.17 | 2022-10-11 | 4 | HRC election; agenda 117 |
| A/78/PV.17 | 2023-10-10 | 3 | HRC election; agenda 114 |
| A/78/PV.23 | 2023-10-31 | 27 | HRC report debate; agenda 66 |
| A/78/PV.28 | 2023-11-07 | 33 | Continued HRC report debate; agenda 66 only |
| A/79/PV.19 | 2024-10-09 | 18 | HRC election; agenda 115 |
| A/79/PV.32 | 2024-11-01 | 36 | HRC report debate; agenda 66 |

Eight files were downloaded from the UN Digital Library; A/77/PV.17 and A/79/PV.32 use the official UN document system. Exact catalogue/document URLs, per-document agenda scope, download timestamps, sizes, and SHA-256 fingerprints are in `Data/manifest.json` and `Data/downloads.json`. Original PDFs remain unchanged. A meeting can include other topics; the full PDF is retained, but only configured agenda sections are indexed. Attribution and agenda boundaries remain heuristic and require review.

## Processing and retrieval

1. `CorpusLoader.cs` checks the manifest, PDF hashes, and parser version. PdfPig extraction handles one/two-column layouts, page boundaries, and publication notices. Document-specific `agendaItems` determine the indexed sections. Speaker labels, group statements, translated chair labels, and HRC president roles are detected heuristically. Non-agenda tribute sections interrupt election indexing.
2. `PassageIndex.cs` splits page segments into at most 800 characters, preferring sentence/word boundaries, with about 120 characters of overlap. Passages never cross speakers or pages. IDs, parent IDs, exact source offsets, and page links are retained.
3. `EmbeddingIndex.cs` uses local `nomic-embed-text`, Nomic's `search_document:`/`search_query:` prefixes, normalized 768-dimensional vectors, and an incremental cache keyed by text and model/chunk version. The query is embedded at request time; the .NET app compares it with stored vectors.
4. `HybridSearch.cs` applies delegation/year/document/capacity filters. Keyword mode uses BM25-style scoring; Semantic uses cosine similarity; Hybrid combines up to 40 candidates per method with reciprocal rank fusion. Keyword rank contributes only with full normalized topic-term coverage; partial lexical matches need semantic support in Hybrid. The cosine threshold is 0.60 and needs independent domain validation. A similarity score is not a confidence probability.
5. `AnswerGenerator.cs` sends up to six passages, at most two per parent intervention, to `qwen2.5:1.5b`. A constrained JSON answer is converted into clickable citations after checking source IDs. Citation validity does not prove factual support. Failed generation retains the evidence. The context viewer and full intervention/PDF links allow verification.

The UI displays at most 200 passages; hybrid pools contain at most 80 candidates before fusion. Counts and CSV rows describe retrieved candidates, not an exhaustive survey. Supported speaker/year conflicts now request clarification before retrieval or generation. Review attribution and claim support in generated answers; citation IDs alone cannot establish factual correctness. No verified procedural fact extraction or automatic election tallies are claimed.

If embeddings are unavailable, the app reports keyword fallback. Start Ollama and restart the app to enable semantic retrieval. Local model endpoints are restricted to loopback. Document/model text is rendered as text; CSV values are protected against formula prefixes. This demo has no production authentication or access controls.

## Data files

| Path | Purpose |
|---|---|
| `Data/manifest.json` | Ten source records, official links, and per-document agenda scope |
| `Data/raw/` | Original official PDFs |
| `Data/downloads.json` | Provenance, download times, sizes, and hashes |
| `Data/extracted/` | Page-labelled extracted text |
| `Data/corpus.json` | Parsed statements and ingestion status |
| `Data/passages.json` | Passage text, parent links, and exact offsets |
| `Data/embeddings.json` | Cached local vectors and model metadata |
| `Data/evaluation.json` | Six inspected HRC development questions and source anchors |
| `Data/retrieval-evaluation.json` | Generated evaluation report, excluded from Git |

Build output, IDE state, private settings, local runtimes/models, logs, and temporary files are ignored. Source code, dependency lock file, PDFs, and reusable data caches are included. `EvidenceSearch.cs` retains the earlier whole-statement implementation and token-normalization helper; the active app uses `HybridSearch.cs`, and current tests target that implementation.

## Rebuild and verification

```powershell
.\Download-Corpus.ps1
dotnet run --no-launch-profile -- --ingest
dotnet run --no-launch-profile -- --rebuild-embeddings --ingest
dotnet run --no-launch-profile -- --self-test
dotnet run --no-launch-profile -- --evaluate
```

`Download-Corpus.ps1` downloads missing PDFs; `-Force` replaces them with current official downloads. Self-tests cover the real HRC corpus, parser boundaries and capacity, filters, source offsets, RRF, embedding cache/recovery, and model citation handling. Stub vectors exercise routing, not semantic accuracy. Live `--evaluate` compares retrieval modes on six development questions; source-anchor and topic-phrase hits are not independent precision/recall or a production accuracy guarantee.

Against a running app:

```powershell
.\Test-Api.ps1
.\Test-LocalGeneration.ps1
```

`Test-ResearchApi.ps1` additionally checks conversation context, year/session refinement, conflicts, group-only retrieval, and comparisons. All three scripts accept `-BaseUrl` to test a separate instance without occupying a Visual Studio port.

`ResearchService.cs` resolves supported follow-ups into typed requests and executes bounded comparisons. Conversation context is client-supplied request data, not trusted identity or authorization.

API endpoints: `POST /api/research`, `GET /api/corpus`, `POST /api/ask`, `POST /api/export`, `GET /api/passages/{id}`, `GET /api/interventions/{id}`, and `GET /api/documents/{id}/pdf`. Ask/export accepts `question`, optional `delegation`/`year`, `groupsOnly`, `includeProcedural`, `generate`, optional `session`, and `searchMode` (`keyword`, `semantic`, `hybrid`).

## Production design and acknowledgements

See `Documentation/Meeting-AI-Azure-Design.md` for the proposed Azure production architecture: approved archive integration, versioned ingestion, managed hybrid search, and separately validated procedural facts and coverage. The local JSON/vector scan is a demo, not the full 20-year service. The technical design, architecture overview, and executive summary describe the current HRC pilot and distinguish its implemented workspace from the proposed Azure service.

Official records belong to the United Nations; source links and provenance are retained. This independent prototype does not imply UN endorsement. PdfPig is Apache 2.0; NuGet versions are pinned. Model references: [Nomic](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5), [Ollama embeddings API](https://docs.ollama.com/api/embed). Codex assisted implementation, source discovery, and verification. Qwen2.5 and Nomic support the local demo; Qwen3 is not used.

`POST /api/research` accepts `{ request, previousRequest?, action?, compareYears? }`. The nested request uses the ask fields. Actions are `ask`, `groups`, `compare`, `previous-year`, and `previous-session`. Responses distinguish `answer`, `comparison`, and `clarification`, and include the resolved request. At most two comparison years are supported. A prior context is required for follow-ups; the server does not retain one between requests.
