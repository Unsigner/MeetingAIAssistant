# Meeting Evidence Assistant

A local .NET hackathon application for researching General Assembly debates on oceans and the law of the sea. It indexes attributed passages, combines keyword and semantic retrieval, and optionally generates cited answers using a small local language model.

## Download, build, and run

This repository contains source code, the four original UN PDFs, extracted text, the structured corpus, searchable passages, and cached embeddings. No prebuilt application, .NET runtime, Ollama binaries, or model weights are included.

Prerequisites on Windows:

- .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
- Ollama: https://ollama.com/download/windows

1. Clone the repository or download and extract its ZIP to a writable folder.
2. Start the Ollama Windows app. Alternatively, run `ollama serve` in a separate terminal.
3. Open a new terminal and download both models once:

```powershell
ollama pull nomic-embed-text
ollama pull qwen2.5:1.5b
```

4. From the project root, restore dependencies, build, and run:

```powershell
dotnet restore
dotnet build
dotnet run --no-build --no-launch-profile
```

5. Open http://localhost:5086 in your browser.

Alternatively, double-click `Start.cmd`; it runs `dotnet run --no-launch-profile`, which restores and builds automatically. You can also open the solution in Visual Studio with .NET 10 support; its launch profile may use a different address. Keep Ollama running. Do not run two app instances on the same port. Press Ctrl+C in the app terminal to stop the web app.

Semantic search and local AI answers are enabled by default in the committed `appsettings.json`; no private configuration or API credentials are required. Qwen2.5 generates answers; Nomic Embed Text creates embeddings. Optional machine-specific overrides belong in `appsettings.Local.json`, which is ignored by Git. The example override file is included.

The demo PDFs and generated data are included, so no corpus download or manual ingestion step is required. If the installed Nomic model differs from the cached model version, the app regenerates embeddings automatically on startup, which may take longer. Keep the folder writable. If Ollama or an embedding model is unavailable, the app reports the limitation and falls back to keyword search. Initial NuGet and model downloads require Internet access; subsequent searches and answers run locally. Official source links require Internet access.

The Git repository includes `Data/raw/`, `Data/extracted/`, `Data/downloads.json`, `Data/corpus.json`, `Data/passages.json`, `Data/embeddings.json`, `Data/manifest.json`, and `Data/evaluation.json`. Build output, published binaries, IDE settings, private overrides, local runtimes/models, logs, temporary files, and generated evaluation reports are excluded. Keep `packages.lock.json` committed for reproducible dependency versions.

## Use

1. Enter a question and optionally select delegation/year/group filters.
2. Select **Hybrid**, **Keyword**, or **Semantic** retrieval. Hybrid is the default.
3. Uncheck **Generate a local AI answer** to compare retrieval without generation latency.
4. Inspect the interpreted scope and actual retrieval mode above the answer.
5. Open passage context, the full intervention, or the exact original PDF page.
6. Click generated citation identifiers to jump to their evidence rows. Export CSV to preserve retrieved candidates and source links.

Useful comparisons:

- `Water is becoming more acidic`
- `Catching fish without reporting it`
- `Sharing scientific know-how with poorer nations`
- `Coastlines moving inland as water rises`
- `plastic pollution`
- `What did Samoa and Tonga say about financing?`
- `plastic pollution in A/78/PV.44`

The natural-language scope parser remains rule-based. Explicit filters take precedence. It recognizes multiple named delegations, a small documented alias map, explicit years and GA PV symbols. It does not implement arbitrary date-language interpretation, autonomous query planning or conversation memory. Each question is independent.

## Data and attribution

The downloaded English UN Digital Library corpus contains 138 PDF pages in four records:

| Symbol | Meeting date | Pages | Official catalogue |
| --- | --- | ---: | --- |
| A/78/PV.43 | 2023-12-05 | 32 | https://digitallibrary.un.org/record/4045203 |
| A/78/PV.44 | 2023-12-05 | 33 | https://digitallibrary.un.org/record/4045724 |
| A/78/PV.48 | 2023-12-18 | 29 | https://digitallibrary.un.org/record/4048288 |
| A/79/PV.52 | 2024-12-12 | 44 | https://digitallibrary.un.org/record/4114820 |

Only agenda sections including item 75 are indexed. The parser detects 104 interventions including procedural speakers. A/78/PV.44 is a technically reissued record dated 3 July 2025. This is a selected corpus, not continuous historical coverage. The UN's official topic list is https://www.un.org/depts/los/general_assembly/general_assembly_records.htm.

PDFs are unchanged. Layout-aware PdfPig extraction identifies speaker boundaries and retains one-based physical PDF page references. Group statements retain both the speaker's delegation and represented group; group members are not expanded into individual speakers. Attribution is heuristic and needs review. OCR, arbitrary SR layouts and comprehensive corrigendum handling are not implemented.

| File or directory | Purpose |
| --- | --- |
| `Data/raw/` | Original PDFs |
| `Data/manifest.json` | Document metadata and official URLs |
| `Data/downloads.json` | Download provenance and PDF checksums |
| `Data/extracted/` | Page-labelled extraction |
| `Data/corpus.json` | Parent interventions and ingestion status |
| `Data/passages.json` | Passage IDs, parent IDs and exact source-page offsets |
| `Data/embeddings.json` | Cached vectors, model digest and chunking version |
| `Data/evaluation.json` | Development queries and inspected source anchors |
| `Data/retrieval-evaluation.json` | Generated evaluation report |

## Retrieval implementation

```text
Official PDFs + metadata
  -> attributed interventions and page segments
  -> overlapping passages of at most 800 characters
  -> BM25 passage index + cached Nomic vectors
Question + validated filters
  -> keyword ranking and/or cosine similarity ranking
  -> reciprocal rank fusion for Hybrid mode
  -> evidence passages, parent intervention context and source pages
  -> optional local Qwen2.5 answer with validated passage citations
```

Passages never cross speaker or PDF page boundaries. They end at sentence/word boundaries where possible and overlap by approximately 120 characters. Each passage stores exact start and length offsets into its parent page text, so source-substring integrity is testable. The current corpus yields 841 passages.

Semantic vectors use Nomic's required `search_document:` and `search_query:` prefixes. Vectors are normalized; cosine similarity is a dot product. The index has 768 dimensions. Model input truncation is disabled: oversized passages cause an explicit indexing error rather than silent loss of text.

The keyword index uses BM25-style scoring with k1=1.2 and b=0.75. Token normalization includes a short explicit synonym list. Full-text statistics are precomputed. The former whole-intervention search implementation remains in `EvidenceSearch.cs` as a baseline for regression tests; the application uses `HybridSearch.cs`.

Hybrid retrieval fuses the top 40 keyword and top 40 semantic candidates using `1/(60 + rank)` from each list, with a keyword rank contribution only when all normalized topic terms are present in the passage. Partial keyword matches need semantic support to appear in Hybrid results; Keyword mode still exposes partial matches. A candidate appearing in both lists receives both contributions when its keyword coverage is complete. Semantic candidates must pass the configured cosine threshold, initially 0.60 for this model/corpus. Similarity is not a confidence probability. The threshold was calibrated on a small development set and needs independent validation before production use.

Generation uses up to six retrieved passages with at most two per parent intervention, reducing domination by one speech. Nomic is an encoder, not another answer-generation LLM. Question embeddings are small local requests; stored passage embeddings are reused at startup when model digest, chunk version and source-text hashes match. Only new/changed text is embedded. Changing an embedding model rebuilds vectors; changing the answer model does not.

If local embedding initialization or a query embedding fails, the app explicitly falls back to keyword search and shows a warning. It never labels fallback output as semantic or hybrid. Query embedding calls have a 15-second deadline; ingestion batches have a longer deadline. Start a stopped Ollama server using the Windows app or `ollama serve`; restart the app if its semantic index did not initialize.

## Counts and completeness

Hybrid/semantic results are ranked candidates, **not all relevant statements**. Their counts describe retrieved distinct parent interventions and delegations, not an exhaustive census. CSV exports all retrieved candidates, not the entire relevant universe. Keyword mode considers all matching passages in the selected scope; the UI displays at most 200. Hybrid mode has at most 80 candidates before fusion/deduplication.

The generated answer sees only a subset of those candidates and must not claim complete coverage. A full-scope relevance-classification workflow and verified election-history database are deferred. Historical election/first-ever claims are refused because the corpus cannot establish them.

## Generation and configuration

`appsettings.Local.json` enables local Qwen2.5. `appsettings.json` leaves generation disabled for a clean checkout. Local settings and environment variables override defaults. Restart the app after configuration changes.

```json
{
  "Generation": {
    "Enabled": true,
    "BaseUrl": "http://127.0.0.1:11434",
    "Model": "qwen2.5:1.5b",
    "MaxEvidence": 6,
    "KeepAlive": "30m",
    "TimeoutSeconds": 300
  },
  "Retrieval": {
    "Enabled": true,
    "EmbeddingModel": "nomic-embed-text",
    "MinSimilarity": 0.60,
    "CandidateLimit": 40
  }
}
```

The answer model uses temperature zero, an 8,192-token context, and at most 700 output tokens. It returns a constrained JSON claim list with source IDs selected from the provided passages. C# validates IDs and builds citation links. ID validity does not establish factual entailment. Unsupported claims, attribution mistakes and repetition remain possible; inspect the source evidence. Invalid model output or unavailable generation leaves retrieved evidence intact. First model loading is slower than warm inference.

No questions or documents are sent to a remote LLM or embedding service. Only loopback HTTP endpoints are accepted. Source/model text is rendered as text, not HTML. CSV cells are protected against formula prefixes. This local demo has no authentication or production authorization controls.

## Rebuild and verify

```powershell
.\Download-Corpus.ps1           # missing PDFs only; -Force replaces with current official downloads
dotnet run --no-launch-profile -- --ingest
dotnet run --no-launch-profile -- --rebuild-embeddings --ingest
dotnet run --no-launch-profile -- --self-test
dotnet run --no-launch-profile -- --evaluate
```

Run `Test-Api.ps1` and `Test-LocalGeneration.ps1` against a running application. Both accept `-BaseUrl`, allowing tests on a separate port while Visual Studio uses its own port.

Self-tests verify parsing, exact source substrings, filters, passage IDs and offsets, rank fusion, vector normalization, cache reuse/invalidation, embedding outage fallback, and model citation validation. Stub vectors test routing correctness; they do not measure semantic model quality.

The live evaluation compares Keyword, Semantic and Hybrid on six development queries, including paraphrases and an out-of-domain negative. It preserves two different measurements: finding the original inspected source anchors at ranks 5/10, and finding an expected topic phrase within the first five results. The second is a weak label, not a full relevance judgment. Equivalent relevant passages may differ from the original anchors. Results and threshold choices must not be presented as held-out precision/recall or a production accuracy guarantee. Some original source anchors remain outside the first ten results even when equivalent topic evidence is retrieved.

## Main code and APIs

- `CorpusLoader.cs`: PDF extraction and intervention attribution.
- `PassageIndex.cs`: page-local passages and source offsets.
- `EmbeddingIndex.cs`: local embedding API, validated vectors and incremental cache.
- `HybridSearch.cs`: interpreted filters, passage BM25, cosine ranking and RRF.
- `AnswerGenerator.cs`: constrained local model generation and validated source IDs.
- `Program.cs`: startup, CLI modes and HTTP endpoints.

Endpoints: `GET /api/corpus`, `POST /api/ask`, `POST /api/export`, `GET /api/passages/{id}`, `GET /api/interventions/{id}`, and `GET /api/documents/{id}/pdf`.

An ask/export request includes `question`, optional `delegation`/`year`, `groupsOnly`, `includeProcedural`, `generate`, and `searchMode` (`keyword`, `semantic`, `hybrid`). Responses disclose actual retrieval mode, interpreted scope, candidate counts and fallback warnings. Evidence includes passage IDs, parent IDs, page links and keyword/semantic rank diagnostics.

## Production path and attribution

This increment deliberately keeps vectors and metadata local. The in-memory BM25/vector scan is adequate for 841 passages; it is not an architecture for the full 20-year archive. A production version should move to PostgreSQL full-text/pgvector or another managed hybrid index, add ingestion queues and revision handling, review attribution, evaluate on independent expert-labelled questions, implement authorization/audit controls, and distinguish exploratory retrieval from exhaustive research.

Official records belong to the United Nations; preserve attribution and source URLs. This independent prototype does not imply UN endorsement. PdfPig is Apache 2.0; NuGet dependencies are pinned in `packages.lock.json`. Nomic embedding model documentation: https://huggingface.co/nomic-ai/nomic-embed-text-v1.5. Ollama embedding API: https://docs.ollama.com/api/embed. Codex assisted implementation, source discovery and verification. Disclose Qwen2.5 and Nomic use in the hackathon AI declaration. Qwen3 was removed and is not used.



