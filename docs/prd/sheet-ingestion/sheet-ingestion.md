# PRD: Sheet Ingestion

## 1. Overview

**Problem.** A user cannot upload an `.xlsx` or `.csv` file today. `FileExtensions` (`Enterprise.Gpt.Dto/Enums/FileExtensions.cs`) already declares `Csv`, `Xls`, `Xlsm` and `Xlsx`, but no `IDocumentTextExtractor` claims any of them — the three registered extractors cover only `.pdf .doc .docx` (`DocumentIntelligenceTextExtractor`), `.pptx` (`PresentationTextExtractor`) and `.md .txt` (`PlainTextExtractor`) — and `UploadedFileValidator` derives its accepted list from `IDocumentTextExtractorFactory.SupportedExtensionNames` (`UploadedFileValidator.cs:75-79`), so a spreadsheet upload is rejected with a 400 before it ever reaches the ingestion pipeline. Even if it were accepted, the existing chunker would not help: `TokenTextChunker`'s paragraph → sentence → token-slice cascade treats a sheet's cell text as prose, producing chunks with no column context and no way to answer an aggregate question like "total revenue by region" — a question embeddings structurally cannot answer no matter how the text is chunked.

**Solution.** Two new local extractors — `.xlsx` via the already-referenced `DocumentFormat.OpenXml` SDK, `.csv` via a small new dependency — feed three distinct retrieval mechanisms that each cover what the others cannot: header-aware **row-window chunks** that keep `document_search` working unchanged; a per-sheet **schema card** chunk so the model can discover what a workbook holds before searching it; and a new deterministic **`sheet_query`** tool that filters and aggregates real cell data with no vector search involved. Cell data is persisted one table row per sheet row, in SQL Server 2025's native `json` column type, across six new tables that mirror the existing chunk/summary sibling-table pattern. The client needs no changes at all: `enterprise-gpt-ui`'s upload picker already derives its accepted extensions from the server (`SupportedExtensionsStore`, `supported-extensions-store.ts:34-44`), and its attachment chip already has icon mappings for `.xlsx` and `.csv` (`attachment.model.ts:16,21`) with a doc comment stating plainly that the table is *"wider than the six formats the API accepts"* — the frontend has been drawn as if this feature exists since before it did.

This PRD ships **before** the File Agent PRD (`docs/prd/file-agent/file-agent.md`), which previously carried a truncated version of this work as US-107/US-108 and now depends on it instead (§9).

**Success criteria.**

- **Format coverage**: 0% of `.xlsx`/`.csv` uploads succeed today (100% rejected with a 400 `validation-error`); target 100% of well-formed `.xlsx` and `.csv` uploads under the existing `Documents:MaxFileSizeBytes` ceiling complete ingestion, measured by `GET api/documents/file-extensions` reporting 8 extensions (up from 6) and an integration test uploading a real workbook and a real CSV through to a `Processed` job status.
- **Row-window integrity**: today there is no row-aware chunking at all; target ≥ 95% of chunks produced from a benchmark workbook large enough to span more than one chunk contain a literal copy of their sheet's header line, measured by a unit test counting header occurrences in `TokenTextChunker`'s output over `SpreadsheetTextExtractor`-produced segments.
- **Deterministic lookup accuracy**: `sheet_query` returns the exact, hand-computed result for 100% of a fixed 20-query benchmark spanning `sum`/`avg`/`min`/`max`/`count`, a grouped aggregate, and a filtered row list, measured by `SheetQueryIntegrationTests` comparing each result to its expected value.
- **Query safety**: 0 SQL statements `sheet_query` generates contain a literal, non-parameterized value or a path string built from unvalidated model input, measured by a `SheetQuerySqlTests` suite pinned the same way `DocumentRetrievalSqlTests` already pins "no literal values in any generated statement" for `document_search`.
- **Ingestion safety**: 100% of sheets exceeding the configured row or column ceiling are refused before any row is persisted, measured by a unit test at the boundary (ceiling, ceiling + 1 row/column).

Elena is a finance analyst with a project that already holds a dozen PDFs. She uploads `q3-budget.xlsx`, a six-sheet workbook. The upload succeeds like any other document; behind it, each sheet becomes a schema card ("Sheet 'Regional Revenue': columns SKU (text), Region (text), Revenue (number), Quarter (text); 4,213 rows") plus a run of header-repeating row-window chunks, and every sheet's cells land in a new set of tables no one but the platform ever queries directly. She asks, "what's in this workbook?" — the model finds the schema cards via `document_search` and describes the six sheets by name. She then asks, "what's the total revenue by region in Q3?" — the model recognizes this as a computation, not a lookup, and calls `sheet_query` with `operation: sum`, `column: Revenue`, `groupBy: Region`, `filters: [{ column: "Quarter", comparator: "eq", value: "Q3" }]`. The tool resolves "Regional Revenue" as the sheet, validates `Revenue`/`Region`/`Quarter` against that sheet's own known columns, and returns four grouped totals computed directly from the stored rows — a number RRF over embeddings could never have produced, however well the sheet had been chunked.

## 2. Goals & non-goals

**Goals.**

- Accept `.xlsx` and `.csv` uploads through the exact extension point every other format already uses: registering an `IDocumentTextExtractor` is what makes the factory, the validator, and the `file-extensions` endpoint advertise the new formats — no new mechanism.
- Make an ingested sheet searchable by `document_search` with no change to that tool, by shaping what the extractor emits rather than teaching `ITextChunker` about tables.
- Let the model discover a workbook's shape — sheet names, columns, inferred types, row counts — before it searches or queries it.
- Answer deterministic questions over real cell data (sums, averages, counts, grouped totals, filtered rows) that vector search structurally cannot, through a new tool that never receives or constructs raw SQL from the model.
- Store cell data in a form a team member can inspect directly in SQL Server, one row per sheet row, using the native `json` column type this repository's compatibility level already selects.
- Ship a working v1 for exactly two formats, with `.xlsm` and `.xls` named as non-goals and their reasons recorded rather than silently deferred.
- Touch nothing in `enterprise-gpt-ui/`: the upload surface, the accept filter, and the attachment chip already handle these two extensions.

**Non-goals.**

- **`.xlsm` (macro-enabled workbook).** OpenXml SDK could parse its cell data, but a macro-bearing file raises a macro-safety question (should macros be stripped, scanned, or the upload refused outright?) this PRD does not answer. Deferred until that decision is made deliberately, not by omission.
- **`.xls` (legacy binary Excel, BIFF/OLE2).** Nothing in the solution reads this format — `DocSharp.Binary.Doc` (already referenced) is a Word-only OLE2 reader, and a spreadsheet-capable OLE2/BIFF library is a new dependency this PRD does not take on for a format Microsoft itself has not shipped a default writer for in over a decade.
- **A frontend surface of any kind.** `enterprise-gpt-ui` needs zero changes; see §6 for the exact evidence.
- **`sheet_query` accepting or constructing raw SQL from the model.** The model may name a sheet, a column, an operation from a closed set, and literal comparison values — nothing else. §6 and EP-4 are written entirely around this constraint.
- **Multi-column `GROUP BY`, joins across sheets, or window functions.** `sheet_query` supports one aggregate operation, one optional single-column grouping, and a small bounded set of equality/comparison filters. A workbook-wide join or a multi-dimensional pivot is out of scope for v1.
- **A second embedding index over cell data.** Rows are retrievable only through `sheet_query`'s deterministic path or through the row-window chunks `document_search` already indexes; no per-cell embedding is generated.
- **Rewriting or reformatting a workbook.** This PRD only reads `.xlsx`/`.csv` on upload; producing a spreadsheet is the File Agent PRD's `xlsx-authoring` skill, unaffected by this work.
- **Updating `docs/documents/retrieval.md` or any other shipped documentation.** A PRD is a pre-implementation artifact; the SE Technical Writer flow updates documentation and the changelog after implementation and review, per house convention.
- **A new permission id.** Uploading a spreadsheet is gated exactly like uploading any other document today (ownership of the target conversation or project); `sheet_query`'s tool attachment reuses the same gate `document_search` already sits behind.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee who can already upload a document into a conversation or project. Uploads a workbook or CSV exactly as they would a PDF; never sees a format picker, a "structured data" toggle, or any indication that two different tools now serve their questions about it.
- **Administrator**: holds `PermissionIds.Administrator`. Nothing in this PRD adds an admin-facing screen or setting; an administrator's only involvement is the existing document- and permission-management surfaces, unchanged.
- **Operator**: runs the deployment. Owns `Sheets:*` and `SheetQuery:*` configuration (row/column ceilings, query bounds, the `SheetQuery:Enabled` flag) and the retrieval re-tune this PRD's EP-5 investigates.
- **Backend engineer**: owns every epic in this document, in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`. There is no frontend engineer role for this feature.

**Role-based access.**

- **Anonymous**: no access. Upload and conversation routes already sit inside groups carrying `.RequireAuthorization()`.
- **Chat user without upload rights to the target conversation/project**: identical to today — ownership is enforced the same way for every document family regardless of extension; a spreadsheet introduces no new authorization branch.
- **Chat user with upload rights**: can upload `.xlsx`/`.csv` exactly as they can any other supported extension, and can ask questions the model may answer via `document_search`, the schema card, or `sheet_query` — whichever fits, decided by the model, not the user.
- **`sheet_query`'s own gate**: attached to a turn under the same conditions `document_search` already uses — the conversation's document scope is non-empty and the selected model supports tools — plus the `SheetQuery:Enabled` flag (§6, §8). No new permission id is introduced; a user who can already see a document's chunks via `document_search` can already see its cells via `sheet_query`, because both close over the identical `DocumentRetrievalScope`.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | `.xlsx` files are accepted as uploads and extracted locally via `DocumentFormat.OpenXml`, one segment family per sheet, ordered from `WorkbookPart.Workbook.Sheets` | P0 | EP-1 |
| FR-2 | `.csv` files are accepted as uploads and extracted locally with BOM detection and comma/semicolon/tab delimiter sniffing | P1 | EP-1 |
| FR-3 | `UploadedFileValidator`'s magic-byte check validates `.xlsx` as a `PK` zip container and `.csv` through the existing no-NULs text heuristic; no new validation mechanism is introduced | P0 | EP-1 |
| FR-4 | `DocumentService.ResolveContentType` covers `.xlsx` and `.csv`, so an uploaded spreadsheet downloads with the correct content type instead of `application/octet-stream` | P0 | EP-1 |
| FR-5 | A malformed, empty, or password-protected spreadsheet fails extraction as a `ValidationException` → 400, matching `PresentationTextExtractor`'s exception-filter pattern | P1 | EP-1 |
| FR-6 | A sheet's data rows are packed into header-aware row-window segments, sized against the chunker's own token budget so a window stays an atomic, unsplit chunking unit in the common case | P0 | EP-2 |
| FR-7 | A row window's text never splits a single row's cell data across two segments | P0 | EP-2 |
| FR-8 | Each ingested sheet produces one schema-card segment naming the sheet, its columns, each column's inferred type, its row count, and up to three sample rows | P1 | EP-2 |
| FR-9 | A passage retrieved from a spreadsheet-origin chunk cites its sheet by name, not by a page number | P1 | EP-2 |
| FR-10 | `Core.ConversationDocumentSheet`/`ProjectDocumentSheet` and their column and row tables persist one row per sheet, one row per column, and one row per data row, mirroring `BaseDocumentChunk`/`BaseDocumentSummary`'s sibling-table split | P0 | EP-3 |
| FR-11 | A sheet row's cell values are stored as one JSON object per table row, in a column explicitly typed `json` | P0 | EP-3 |
| FR-12 | No index supporting `sheet_query`'s lookups is built on the `json` column itself; every supporting index sits on an ordinary typed column | P0 | EP-3 |
| FR-13 | A sheet, its columns, and its rows are written inside the same `SaveChangesAsync` call that persists the document and its chunk rows | P0 | EP-3 |
| FR-14 | The SQLite in-memory unit-test fixture loads the model with the new tables present, so every other entity's unit tests keep passing unaffected | P1 | EP-3 |
| FR-15 | The model can name a sheet and a column and receive a computed aggregate (`sum`/`avg`/`min`/`max`/`count`), optionally grouped by a second column | P0 | EP-4 |
| FR-16 | The model can filter a sheet's rows by one or more column comparisons and receive the matching rows, capped at a configured maximum | P1 | EP-4 |
| FR-17 | An unknown or ambiguous sheet or column name is refused by name, listing what is actually available, rather than guessed | P0 | EP-4 |
| FR-18 | Every `sheet_query` SQL statement is built from a closed set of operations and comparators, with every literal value bound as a parameter and every JSON path built only from an already-validated column name; no model-supplied text is ever concatenated into the command | P0 | EP-4 |
| FR-19 | `sheet_query` caps its result row count and its own execution time, both distinct from `document_search`'s own caps | P0 | EP-4 |
| FR-20 | `sheet_query` is attached to a turn under the same conditions `document_search` already uses, stands down if `document_search` itself is unavailable, and stands down on a name collision with an MCP tool | P0 | EP-4 |
| FR-21 | A sheet exceeding a configured row or column ceiling is refused before ingestion persists anything for it | P0 | EP-5 |
| FR-22 | `MaxDistance` and the other retrieval defaults are re-validated against a header-repeated row-window corpus, and adjusted or the current defaults are confirmed adequate | P1 | EP-5 |
| FR-23 | Sheet ingestion and `sheet_query` activity emit spans and metrics through the existing OpenTelemetry registration, with no cell content or document identity in any tag | P1 | EP-5 |
| FR-24 | `sheet_query`'s tool attachment sits behind a configuration flag, defaulting off, with a documented rollback requiring no redeploy | P0 | EP-5 |

## 5. User experience

**There is no new screen, upload flow, or affordance.** `enterprise-gpt-ui`'s composer already accepts whatever `GET api/documents/file-extensions` reports (`SupportedExtensionsStore.load`), and its attachment chip already renders `.xlsx` and `.csv` with the correct icon and tone (`attachment.model.ts:16-21`) — a fact confirmed directly in that file's own doc comment: *"Wider than the six formats the API accepts, because the same chip renders a file the user picked before anything validated it — a `.xlsx` refused as unsupported should still look like a spreadsheet while it says so."* Once the server accepts the two extensions, the client's existing code path renders them correctly with zero changes.

**Entry points & first-time flow.** A user drags a workbook or CSV into the composer or the project file picker exactly as they would a PDF. Nothing distinguishes the moment of upload; the job progresses through the same eight `JobStatus` stages (`Queued` → `Uploading` → `Extracting` → `Chunking` → `Embedding` → `Persisting` → `Processed`/`Failed`) every other format already uses.

**Core experience.**

1. The user uploads `.xlsx` or `.csv`; the existing upload progress UI tracks it identically to any other format.
2. In a later turn, the user asks a question. If it is a natural-language question about the sheet's *content* ("what's the SKU for the blue widget"), the model calls `document_search` and a row-window passage answers it, citing its sheet by name.
3. If the question is exploratory ("what's in this file", "what sheets does this have"), the model finds the schema-card chunk via the same `document_search` call and describes the workbook's shape.
4. If the question is a computation ("total revenue by region", "how many rows have status = late", "average order size in March"), the model calls `sheet_query`, which returns a computed result with no vector search involved.
5. The answer folds the tool's result into prose exactly as `document_search`'s passages already are — no new UI element renders around it.

**Edge cases & UI states.**

- **Malformed or password-protected spreadsheet**: the upload fails with the existing validation-error UI, naming the file as unreadable — no new error surface.
- **A sheet or column the model names that does not exist**: `sheet_query` refuses inline, and the model's answer explains what is actually available; no error reaches the user as a failed turn.
- **A workbook exceeding the row/column ceiling**: the upload fails with a validation error naming the limit, before any partial ingestion happens.
- **`SheetQuery:Enabled` off**: `sheet_query` is never attached; from the user's perspective this is indistinguishable from "the model didn't think a computation was needed" — document_search and the schema card still work.

**UI/UX highlights.** None apply beyond what already exists — this is the entire point of §6's frontend-impact finding below.

## 6. Technical considerations

**Four findings shape the design below, verified directly against the working tree and, where noted, against live Microsoft Learn documentation rather than assumed from the locked decisions alone.**

**1. `enterprise-gpt-ui` needs no code change at all.** `SupportedExtensionsStore` (`enterprise-gpt-ui/src/app/core/documents/supported-extensions-store.ts:34-44`) derives its accepted-extension list purely from `GET api/documents/file-extensions` with no hardcoded fallback set, and its own doc comment states the server "cannot advertise a format nothing can parse — which is why the picker and the client-side refusal both read it instead of hard-coding a set." `attachment.model.ts:14-24`'s `BY_EXTENSION` map already carries `xlsx: { icon: 'bi-file-earmark-excel', tone: 'ok' }` and `csv: { icon: 'bi-filetype-csv', tone: 'accent' }`, tested by `attachment-chip.spec.ts:14,16`. Both icons are already in the checked-in sprite manifest (`icon-names.ts:51,57`). This is why §2 and §5 state zero frontend impact as a fact, not an aspiration.

**2. Header-aware chunking is shaped inside the extractor, not a new `ITextChunker`.** `ITextChunker.Chunk(segments, ct)` (`Enterprise.Gpt.Service/Chunking/ITextChunker.cs`) knows nothing about a source format's structure — teaching it tabular semantics would leak spreadsheet-specific concepts into an abstraction every other extractor also depends on. Instead, `SpreadsheetTextExtractor` and `CsvTextExtractor` emit `DocumentSegmentDto`s already shaped as row windows: each window's text begins with a literal copy of the sheet's header line, followed by its own data rows, each on its own line (`\n`-joined, no blank line inside the window). This exploits — deliberately, and verified by reading `TokenTextChunker.cs` rather than assumed — two real properties of the existing chunker: `SplitParagraphs` treats a blank-line-free block of text as one indivisible unit (`ParagraphBoundary`, `\n[ \t]*\n[\s]*`) as long as it fits under `MaxTokens`, and `SentenceBoundary`'s own regex splits on `\n` as one of its two boundary conditions — so even the fallback path a too-large window would hit splits it exactly on row boundaries, never mid-row, satisfying FR-7 without a special case. **The corollary, stated as a limitation rather than solved:** the chunker's overlap mechanism (`BuildOverlap`) can seed a chunk with a token-level *tail* of a previous window's last rows, without that window's own header — and a window that itself exceeds `MaxTokens` degrades to per-row units with the header retained only in its own leading unit. Both are accepted, bounded failure modes rather than defects, provided windows are kept comfortably under the budget: `SpreadsheetTextExtractor`/`CsvTextExtractor` size a window by calling the injected `ITextChunker.CountTokens`/`MaxTokens` directly, targeting `Sheets:RowWindow:TargetTokenFraction` (default 0.75, so ~384 of 512 tokens) and capping row count at `Sheets:RowWindow:MaxRows` (default 50) as a backstop for sheets with very short rows. This is why FR-6's success criterion is a **measured 95% threshold**, not a guarantee.

**3. Structured cell storage needs a second extraction contract, not a change to `IDocumentTextExtractor`.** `IDocumentTextExtractor.ExtractAsync` returns only `IReadOnlyList<DocumentSegmentDto>` — text, nothing structural — and every other extractor is built against exactly that shape. Widening it for two extractors would force every implementation (and every test double) to answer a question only two of them have. Instead, a new interface, `ISheetStructureExtractor : IDocumentTextExtractor` (`Enterprise.Gpt.Service/Extraction/ISheetStructureExtractor.cs`), adds one method — `Task<SheetExtractionResult> ExtractSheetsAsync(FileDto, IProgress<DocumentExtractionProgress>?, CancellationToken)` — returning both the text segments **and** the structured per-sheet column/row data from a single parse of the workbook (parsing the OpenXml package twice per upload would double extraction cost for no benefit). `ExtractAsync` on both new extractors simply projects `ExtractSheetsAsync`'s segments. `DocumentService`'s ingestion path type-checks `is ISheetStructureExtractor` and, when true, persists the returned `SheetStructureDto`s alongside the chunk rows in the same save (FR-13) — every other extractor's path is unchanged.

**4. The `Cells` column is an explicit `HasColumnType("json")` override, not EF Core 10's complex-type auto-selection — confirmed against Microsoft Learn, not assumed from the locked decision's wording.** EF Core 10's *automatic* JSON-type selection at compatibility level ≥ 170 applies specifically to a complex type mapped with `.ToJson()` or to a primitive collection (`https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/whatsnew#complex-types`) — neither fits a per-sheet cell bag whose keys vary by workbook. `BaseDocumentSheetRow.Cells` is instead a plain `string` property holding hand-serialized JSON text (`System.Text.Json`, one property per column, keyed by column name), with an **explicit** `.HasColumnType("json")` — a documented, supported override path ("*you can override the column type with `HasColumnType` if needed*," same page) that gives this PRD's own serialization code full control rather than relying on complex-type inference. Query-time extraction uses `JSON_VALUE(Cells, @path RETURNING <type>)`, confirmed to accept a **parameterized path** since SQL Server 2017/Azure SQL Database (`https://learn.microsoft.com/sql/t-sql/functions/json-value-transact-sql`) — the mechanism EP-4's no-raw-SQL design below depends on. One further confirmed constraint: `RETURNING`'s supported type list has **no `bit`/boolean entry** (`tinyint, smallint, int, bigint, decimal, numeric, float, real, char, varchar, varchar(max), nchar, nvarchar, nvarchar(max), date, time, datetime2, datetimeoffset`), so a `Boolean`-typed column compares as text (`'true'`/`'false'`) rather than `RETURNING bit`.

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| The extension point every new format goes through | `Enterprise.Gpt.Api/Program.cs:662-671` — three `AddSingleton<IDocumentTextExtractor, …>()` registrations plus `IDocumentTextExtractorFactory`/`ITextChunker`; the comment there: *"Registering an `IDocumentTextExtractor` is all it takes to support a new format."* `DocumentTextExtractorFactory` (`Enterprise.Gpt.Service/Validators/UploadedFileValidator.cs`'s sibling file, `Extraction/IDocumentTextExtractorFactory.cs`) throws at construction if two extractors claim one extension |
| The template to copy for `.xlsx` | `Enterprise.Gpt.Service/Extraction/PresentationTextExtractor.cs` — local OpenXml parse under a named exception filter (`OpenXmlPackageException`, `FileFormatException`, `InvalidDataException`, `XmlException`, `ArgumentOutOfRangeException`, `KeyNotFoundException`) → `ValidationException` → 400; `new MemoryStream(file.Content, writable: false)`; ordering from the document's own ordered list (`SlideIdList`, not part order — the workbook analogue is `WorkbookPart.Workbook.Sheets`, not `WorksheetParts`); one `progress?.Report` per unit |
| The template to copy for BOM/encoding handling | `Enterprise.Gpt.Service/Extraction/PlainTextExtractor.cs` — `StreamReader` with `detectEncodingFromByteOrderMarks: true`, UTF-8 fallback, `ReplaceLineEndings("\n")` |
| The package `.xlsx` needs — already referenced | `Enterprise.Gpt.Service/Enterprise.Gpt.Service.csproj:26` — `DocumentFormat.OpenXml` 3.5.1; its comment ("PowerPoint (.pptx) text extraction") is updated to also name spreadsheet extraction |
| The package `.csv` needs — new dependency, decided | `Sylvan.Data.Csv` (MIT-licensed, zero dependencies, ~1.4.x latest at time of writing) rather than a hand-rolled RFC 4180 reader — a correct reader has to handle quoted multi-line fields, escaped quotes, and mixed encodings, and getting any of those wrong would silently corrupt data `sheet_query`'s aggregates depend on being right, for something the library already solves and tests |
| The validator, and the exact arms to extend | `Enterprise.Gpt.Service/Validators/UploadedFileValidator.cs:96-111` — `HasContentMatchingExtension`'s `switch`; `.Docx or .Pptx => StartsWith(content, _zipSignature)` gains `.Xlsx`, and `.Md or .Txt => !LooksBinary(content)` gains `.Csv`. The `_ => true` default is why this is load-bearing, not cosmetic — without the extension, either format would pass unvalidated |
| Content-type map to extend | `Enterprise.Gpt.Service/DocumentService.cs:591-598` — the six-entry `FrozenDictionary` `_contentTypes`; `download-workflow.md` §4.3 states it "covers the six formats ingestion accepts," which becomes false once this ships (a documentation update for the technical-writer flow, not this PRD) |
| Segment and chunk vocabulary | `Enterprise.Gpt.Dto/DocumentSegmentDto.cs` — *"a page for PDF and Word, a slide for PowerPoint, the whole file for plain text and Markdown"*; this PRD adds "a row window or a schema card, for a sheet." `TextChunkDto` (`Enterprise.Gpt.Dto/TextChunkDto.cs`) is unchanged |
| The chunker whose token budget row windows target, unchanged | `Enterprise.Gpt.Service/Chunking/TokenTextChunker.cs` — `MaxTokens`/`OverlapTokens`/`CountTokens`, `Documents:Chunking:MaxTokens` = 512, `:OverlapTokens` = 128 (`Settings/DocumentOptions.cs`) |
| The citation line to change | `Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs:608-617` — `Citation = page.HasValue ? $"{documentName} p.{page.Value}" : documentName`; a sheet-origin chunk needs a distinguishable citation shape (for example `"{documentName} — {sheetName}"`), which is the one place this PRD touches a file `document_search` itself owns |
| The lookup `sheet_query` sits beside, and whose shape it copies exactly | `Enterprise.Gpt.Service/Tool/DocumentRetrievalSql.cs` — hand-written, fully parameterized T-SQL; `ScopedChunks`'s `UNION ALL` of the two chunk tables is the pattern `sheet_query`'s own sheet/row resolution mirrors; `BuildLexicalSearch`'s "the number of arms varies, no value is ever concatenated" discipline is the pattern its aggregate/filter builder copies; pinned today by `DocumentRetrievalSqlTests`' "no literal values in any generated statement" assertion, which `SheetQuerySqlTests` mirrors |
| The tool shape to copy | `Enterprise.Gpt.Service/Tool/DocumentTool.cs` — static `Create(scope, …)` factory returning an `AIFunction`, a private per-turn invoker, `[Description]` on parameters, `ChatProgress.Report`, a sanitized `InvalidOperationException` on failure |
| Name-matching shared with the other document tools | `Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs` — `internal static IReadOnlyList<RetrievableDocument> MatchByName(DocumentRetrievalScope, string)`; `sheet_query` needs an equivalent sheet-level matcher over the new sheet tables, refusing an unknown or ambiguous name exactly as `document_summarize` already does for document names |
| Where every tool is attached | `ConversationService.CreateChatOptionsAsync` (`ConversationService.cs:2127` onward) — `attachDocumentTool`/`attachSummaryTool`'s gate ladder (lines 2198-2314) is the pattern `attachSheetQueryTool` copies exactly, including the "stands down if `attachDocumentTool` is false" rule `attachSummaryTool` already establishes, and the MCP name-collision stand-down each existing tool performs |
| MCP attribution rule the tool name must not trip | `Enterprise.Gpt.Service/Chat/UsageReportTranslator.cs:313` — `ResolveMcpServerId`, longest `{sanitizedServerName}_` prefix match. `sheet_query` shares no prefix with `document_*`, carrying the same residual, acknowledged risk `document_search`/`document_summarize` already carry for their own naming |
| Table shape to copy | `Enterprise.Gpt.Repository/Migrations/20260825224934_AddDocumentSummaries.cs` plus `Enterprise.Gpt.Entity/BaseDocumentSummary.cs`/`ConversationDocumentSummary.cs`/`ProjectDocumentSummary.cs` and their configurations — schema `Core`, sibling tables per document family, `[Timestamp] byte[] Version`, soft delete with **no** `HasQueryFilter`, every FK forced to `DeleteBehavior.NoAction` by the global loop in `EnterpriseGptDbContext.OnModelCreating` (no per-configuration opt-in needed) |
| Where configurations are registered | `Enterprise.Gpt.Repository/EnterpriseGptDbContext.cs:51-68` — nineteen `modelBuilder.ApplyConfiguration(...)` calls today; this PRD adds six more, one per new table, applied explicitly like every other |
| The SQLite adaptation to extend | `tests/Enterprise.Gpt.Unit.Test/TestInfrastructure/SqliteRowVersionModelCustomizer.cs` — its `property.SetColumnType(null)` runs unconditionally on every declared property before the `Version`/`SqlVector<T>` special cases, which is the same mechanism that already neutralizes `[Column(TypeName = "nvarchar(max)")]` on `BaseDocument.Path`; §6 finding 4 above is why this PRD expects `Cells`'s `json` override to be handled by that existing pass rather than needing `SqlVector<T>`-style removal — proven by a test, not assumed |
| Test fixture precedent to mirror | `tests/.../TestInfrastructure/PresentationBuilder.cs` — builds `.pptx` bytes in memory with OpenXml, including deliberately corrupt variants; a new `WorkbookBuilder` mirrors it for `.xlsx` |
| Ingestion pipeline, and the one save the new tables join | `Enterprise.Gpt.Service/DocumentService.cs` — stage bands `UploadStart = 0`, `ExtractStart = 10`, `ChunkStart = 45`, `EmbedStart = 50`, `PersistStart = 92`; `IngestAsync` (line 655) wraps extraction/chunking/embedding in a `try` that best-effort deletes the blob (`TryDeleteBlobAsync`) on failure; `document.Chunks = BuildChunks<…>(…)` then one `_ctx.Add(document); await _ctx.SaveChangesAsync(…)` (lines 297-313), followed by the post-save parent-still-active recheck (lines 315-329) — sheet/column/row rows are added to the same `document` graph before that single save |
| Retrieval quality risk this PRD inherits | `docs/documents/retrieval.md` §11.2 — *"The keyword pass is substring matching… aimed at identifiers, not at natural-language recall"* (favorable: spreadsheet content **is** identifiers) but *"`MaxDistance` is a single global threshold"* — header-repeated row chunks change a corpus's distance profile, which EP-5 investigates rather than assumes fine |
| No existing JSON SQL usage in the repository | Confirmed by a solution-wide search: zero call sites of `OPENJSON`, `JSON_VALUE`, `JSON_QUERY`, `ISJSON`, or `FOR JSON` exist today — this PRD's migration is the first |

**Data storage & privacy.**

- **Schema shape**: `BaseDocumentSheet` (`SheetIndex` 1-based matching a row-window chunk's `SourceNumber`, `SheetName` `[StringLength(256)]`, `RowCount`, `ColumnCount`) → `Core.ConversationDocumentSheet`/`Core.ProjectDocumentSheet`; `BaseDocumentSheetColumn` (`ColumnIndex` 0-based, `ColumnName` `[StringLength(256)]`, `InferredType` — a new `SheetColumnType { Text = 1, Number = 2, Date = 3, Boolean = 4 }` enum, `HasConversion<int>()`, numbered from 1 matching `JobStatus`'s own convention) → `Core.ConversationDocumentSheetColumn`/`Core.ProjectDocumentSheetColumn`; `BaseDocumentSheetRow` (`RowIndex` 0-based among data rows, `Cells` — the `json`-typed string) → `Core.ConversationDocumentSheetRow`/`Core.ProjectDocumentSheetRow`. Six mapped tables, three abstract unmapped bases — the sixth-through-nineteenth… twentieth-through-twenty-fifth configuration registrations.
- **Uniqueness differs from the summary tables' shape.** A document can hold many sheets, so the sheet tables are unique on **(owning document id, `SheetIndex`)**, not one-per-document; column tables are unique on **(sheet id, `ColumnIndex`)**; row tables are unique on **(sheet id, `RowIndex`)** — all three filtered `[DateDeactivated] IS NULL`, matching `ConversationDocumentChunkConfiguration`'s own ordinal-index precedent so a soft-deleted sheet does not block a later re-ingestion of the same document.
- **The `json` type is still preview on SQL Server 2025 (17.x), GA only on Azure SQL Database/MI under the 2025 update policy.** This is a stated deployment risk, not a hidden one: the named fallback is `nvarchar(max)` with an `ISJSON` check constraint, switched by configuring a compatibility level below 170 (documented Microsoft mitigation) or by an explicit `HasColumnType("nvarchar(max)")` override on `Cells` alone, without touching the other five tables.
- **A `json` column cannot be an index key.** Every supporting index this PRD adds — the three uniqueness indexes above — sits on ordinary typed columns (`SheetIndex`, `ColumnIndex`, `RowIndex`, each an `int`); `Cells` never appears in an index key, only ever read through `JSON_VALUE`.
- **Hard SQL Server JSON caps — 65,535 elements per array, 65,535 properties per object, 32K unique keys, 7,998 bytes per key — are why decision 2 is shaped one-object-per-row rather than one-array-per-sheet.** A sheet with Excel's own row ceiling (1,048,576) would blow past the array cap as a single JSON document; one JSON object per row, bounded to `ColumnCount` properties (itself capped at `Sheets:MaxColumnsPerSheet`, default 200, far under 65,535), never approaches any cap.
- **Migration placement.** All six tables ship in **one** migration — the **fifteenth**, following `20260825224934_AddDocumentSummaries` — matching how `AddDocumentSummaries` itself shipped two sibling tables together.
- **Cell values never ride telemetry.** Spans and metrics (EP-5) carry a deployment name, an operation, and an outcome — never a sheet name, a column name, a cell value, or a document identity.
- **A sheet's soft delete follows its document's**, in the same transaction, the same way a chunk row already does — no new cascade path is introduced because the sheet/column/row rows hang off the same `ConversationDocument`/`ProjectDocument` foreign key chain the chunk rows already do.

**Security.**

- **`sheet_query` never receives or builds SQL from the model.** The model supplies a sheet name, a column name, an operation from `{sum, avg, min, max, count, filter}`, an optional group-by column, and up to `SheetQuery:MaxFilters` (default 5) `{column, comparator, value}` filters, comparators drawn from `{eq, neq, gt, gte, lt, lte, contains}`. The service resolves the sheet and every named column against that sheet's own `SheetColumn` rows **first** — an ordinary parameterized lookup — before building any statement; an unresolved name refuses by name (FR-17) rather than falling through to a query built on a guess.
- **Every JSON path used in a generated statement is built exclusively from an already-validated `ColumnName`, and is itself passed as a bound `@path` parameter to `JSON_VALUE`/`OPENJSON`** (confirmed parameterizable per §6 finding 4) — never as string-interpolated SQL text. The `RETURNING` type keyword is chosen by a closed C# `switch` over the column's own stored `InferredType`, selecting between a small, fixed number of pre-authored parameterized command texts — mirroring exactly how `DocumentRetrievalSql.BuildLexicalSearch` varies only the *number* of `LIKE` arms per query while never concatenating a value into the command text. Every literal comparison value is bound as an ordinary SQL parameter.
- **Ownership is enforced by scope, not by a new authorization branch.** `sheet_query` closes over the same `DocumentRetrievalScope` `document_search` and `document_summarize` already resolve once per turn; a sheet outside that scope simply cannot be named.
- **Result size and time are bounded independently of `document_search`'s own caps** (`SheetQuery:MaxResultRows` default 50, `:MaxGroups` default 200, `:TimeoutSeconds` default 10) — a `TOP (@n)`-style cap on every statement, matching `DocumentRetrievalSql.DenseSearch`'s own `TOP (@candidates)` precedent.
- **No new secret or credential surface.** `sheet_query` reads through the same `EnterpriseGptDbContext`/connection every other repository call already uses.

**Scalability & performance.**

- **Ingest-time ceilings are the real operational bound, not SQL Server's own hard caps.** `Sheets:MaxRowsPerSheet` (default 20,000) and `Sheets:MaxColumnsPerSheet` (default 200) are enforced by the extractor before any row is returned for persistence — refused outright, at zero storage cost, rather than truncated silently (a truncated sheet would make `sheet_query`'s aggregates quietly wrong).
- **`sheet_query` costs no embedding call and no chat-model call of its own** — it is deterministic SQL executed synchronously inside the tool invocation, tracked as an ordinary `Function`-kind tool call exactly as `document_search` already is; no new `ConversationUsageKinds` value or billing mechanism is needed.
- **A large row table (up to 20,000 rows × 200 columns) is scanned, not indexed, for an aggregate** — acceptable at this ceiling for the same reason `DocumentRetrievalSql`'s exact kNN scan is acceptable at its own scale (§8.1 of `retrieval.md`): the candidate set is always narrowed to one sheet, within one conversation or project, before any computation runs.
- **`MaxDistance`'s single global threshold (`retrieval.md` §11.2) is a genuine risk, not a settled one.** Header-repeated row-window chunks change a corpus's distance profile relative to the prose the default was tuned against; EP-5's `US-502` re-validates rather than assumes the existing 0.62 default still holds.

**AI system requirements.**

- **Tools the system needs**: no new model or provider — `sheet_query` runs entirely in T-SQL against the existing `EnterpriseGptDbContext` connection, with no `IChatClient` call of its own.
- **Evaluation strategy**: a fixed 20-query benchmark (the same one the success criteria reference) spanning every operation, one grouped case, and one filtered-list case, each with a hand-computed expected result; 100% must match exactly, since a deterministic tool with a wrong answer is strictly worse than no tool at all.
- **Tool naming risk, stated explicitly**: usage is attributed to an MCP server by longest `{server}_` prefix match (`UsageReportTranslator.ResolveMcpServerId`). `sheet_query` shares no prefix with `document_search`/`document_summarize`, so it carries its own, independent instance of the same acknowledged, unresolved risk those two already carry — a code-search-backed test asserts no catalog MCP server sanitizes to a colliding `sheet_query_` prefix, exactly as the other two tools are already tested.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Accept a spreadsheet at all | `.xlsx` and `.csv` extractors, the two validator arms, the content-type map, the eight-extension surface | P0 | M | — |
| EP-2 | Structure-aware chunking | Header-preserving row windows, the per-sheet schema card, sheet-aware citation | P0 | M | EP-1 |
| EP-3 | Sheet schema & cell storage | Six new tables, one migration, the `json` cells column, the SQLite fixture, persistence inside the existing save | P0 | L | EP-1 |
| EP-4 | The `sheet_query` lookup | A deterministic, bounded, injection-proof filter/aggregate tool beside `document_search` | P0 | L | EP-2, EP-3 |
| EP-5 | Scale, safety & governance | Row/column ceilings, the `MaxDistance` re-tune, telemetry, flag and rollback | P0 | M | EP-2, EP-4 |

EP-1 and EP-2 are ordered first because everything downstream needs real, well-shaped segments to work against; EP-1 is narrowly "can a spreadsheet get in at all" (the same gate `UploadedFileValidator`'s `_ => true` default makes load-bearing rather than cosmetic), while EP-2 is where the two locked retrieval mechanisms that ride the *existing* pipeline — row windows and the schema card — actually get built, plus the one change inside `document_search` itself (the citation fix). EP-3 can start as soon as EP-1's extractors exist to feed it, and is sized L because six tables, one migration, and a genuinely uncertain SQLite adaptation is real, if mechanical, work — but no single story in it exceeds M. EP-4 is the epic the other four exist to serve: the third locked retrieval mechanism, and the one carrying this PRD's one hard security requirement (no raw SQL, ever), so its own decomposition front-loads name resolution and the parameterized-SQL builder before either user-facing verb (aggregate, then filter) is built on top of them. EP-5 closes last because a row/column ceiling, a retrieval re-tune, and a rollback path are only worth exercising against a feature that already does something — not because any of its three stories is architecturally coupled to a specific EP-4 story finishing first.

### EP-1: Accept a spreadsheet at all

#### US-101: `[enabler]` Register the `.xlsx` extractor

- **Story**: `[enabler]` Add `SpreadsheetTextExtractor` (`Enterprise.Gpt.Service/Extraction/SpreadsheetTextExtractor.cs`), registered as an `AddSingleton<IDocumentTextExtractor, SpreadsheetTextExtractor>()` beside the existing three in `Program.cs`, reading `.xlsx` locally with `DocumentFormat.OpenXml` and emitting one segment family per sheet ordered from `WorkbookPart.Workbook.Sheets` (not `WorksheetParts`). Extends `UploadedFileValidator`'s `_zipSignature` arm to cover `FileExtensions.Xlsx`. Unblocks US-102, US-103, US-201.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given the extractor is registered, when `GET api/documents/file-extensions` is called, then it reports `.xlsx` among its extensions, derived from the factory rather than hand-edited.
  - Given a multi-sheet workbook, when it is uploaded, then each sheet's `SourceNumber` in its produced segments is its 1-based ordinal position from `WorkbookPart.Workbook.Sheets`, mirroring how `PresentationTextExtractor` numbers slides from `SlideIdList`.
  - Given the magic-byte check `UploadedFileValidator.HasContentMatchingExtension` already performs, when an `.xlsx` is uploaded, then it is validated as a `PK` zip container via the same arm `.docx`/`.pptx` already use — the extension is added to an existing arm, not a new one.
  - Given a workbook with a sheet that has no rows, when it is extracted, then it produces zero segments for that sheet rather than throwing.
  - Given `Enterprise.Gpt.Service.csproj`, when its `DocumentFormat.OpenXml` comment is reviewed, then it names spreadsheet extraction alongside PowerPoint extraction — no new package reference is added.

#### US-102: `[enabler]` Register the `.csv` extractor

- **Story**: `[enabler]` Add `CsvTextExtractor` (`Enterprise.Gpt.Service/Extraction/CsvTextExtractor.cs`), reading `.csv` via `Sylvan.Data.Csv`, with BOM detection matching `PlainTextExtractor`'s pattern and delimiter sniffing across comma, semicolon, and tab. Extends `UploadedFileValidator`'s `.Md or .Txt` no-NULs arm to also cover `FileExtensions.Csv`. Unblocks US-103, US-202.
- **Priority**: P1 · **Estimate**: M · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given the extractor is registered, when `GET api/documents/file-extensions` is called, then it reports `.csv` among its extensions.
  - Given a semicolon- or tab-delimited file with a `.csv` extension, when it is extracted, then its columns are recognized correctly rather than collapsed into one column.
  - Given the magic-byte check, when a `.csv` is uploaded, then it is validated through the same no-NULs heuristic `.md`/`.txt` already use.
  - Given a CSV with a UTF-16 byte-order mark, when it is extracted, then it decodes correctly, matching `PlainTextExtractor`'s own BOM-detection behavior.
  - Given a `.csv` file, when it is modeled for storage (EP-3), then it is treated as a single-sheet workbook — `SheetIndex = 1`, a derived sheet name — so `sheet_query` and the schema card work identically for both formats.

#### US-103: `[enabler]` A malformed or protected spreadsheet fails cleanly

- **Story**: `[enabler]` Wrap both new extractors' parse logic in `PresentationTextExtractor`'s named exception-filter pattern (`OpenXmlPackageException`, `FileFormatException`, `InvalidDataException`, `XmlException`, `ArgumentOutOfRangeException`, `KeyNotFoundException` for `.xlsx`; a malformed-CSV equivalent for `.csv`), converting to a `ValidationException` → 400 rather than an opaque 500. Unblocks US-101 and US-102's own corrupt-input test cases.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-101, US-102
- **Status**: Not started
- **Acceptance criteria**:
  - Given a truncated or corrupt `.xlsx` (built with a `WorkbookBuilder` test fixture mirroring `PresentationBuilder`), when it is uploaded, then extraction fails as a `ValidationException` naming the file as unreadable, not an unhandled exception.
  - Given a password-protected `.xlsx`, when it is uploaded, then it fails the same way rather than hanging or throwing an unfiltered exception.
  - Given a `.csv` with malformed quoting Sylvan's reader rejects, when it is uploaded, then it fails as a `ValidationException` with the same shape.
  - Given `DocumentService.ResolveContentType`, when its map is read after this story, then it also covers `.xlsx` (`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`) and `.csv` (`text/csv`) alongside the six extensions it covers today.

### EP-2: Structure-aware chunking

#### US-201: `[enabler]` Header-aware row windows for `.xlsx`

- **Story**: `[enabler]` Extend `SpreadsheetTextExtractor` to pack each sheet's data rows into row-window segments: each window's text opens with a literal copy of the sheet's header row, followed by that window's own data rows (one per line, no blank line inside the window), sized via the injected `ITextChunker.CountTokens`/`MaxTokens` to target `Sheets:RowWindow:TargetTokenFraction` (default 0.75) of the chunker's own `MaxTokens`, capped at `Sheets:RowWindow:MaxRows` (default 50) rows per window regardless of token count. Unblocks US-203, US-204, US-501.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101
- **Status**: Not started
- **Acceptance criteria**:
  - Given a sheet with a header row and more data than fits one chunk, when it is chunked by the unmodified `TokenTextChunker`, then at least 95% of the produced chunks contain a literal copy of the header line — the PRD's own measured success criterion, not an assumption.
  - Given a row window's text, when it is inspected, then no single data row's cell text is split across two segments — verified by constructing a window whose combined text exceeds `MaxTokens` and confirming the chunker's own sentence-boundary fallback (which splits on `\n`) never separates a row's own cells from each other.
  - Given a sheet with no discernible header row (for example a single-column dump with no distinct first row), when it is chunked, then it falls back to one row window with no header line rather than inventing column names.
  - Given the row-window sizing, when a sheet has very short rows, then `Sheets:RowWindow:MaxRows` caps the window regardless of how much token budget remains.

#### US-202: `[enabler]` Header-aware row windows for `.csv`

- **Story**: `[enabler]` Apply the identical row-window shaping from US-201 inside `CsvTextExtractor`, treating the whole file as one sheet. Unblocks US-203, US-204, US-501.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-102, US-201
- **Status**: Not started
- **Acceptance criteria**:
  - Given a CSV with a header row and more data than fits one chunk, when it is chunked, then at least 95% of produced chunks contain the header line, matching US-201's own threshold.
  - Given the same file, when its row windows are compared to a `.xlsx` sheet's, then they follow the identical sizing rule (`Sheets:RowWindow:TargetTokenFraction`/`:MaxRows`) — one shared implementation, not a second one.

#### US-203: `[enabler]` Per-sheet schema card

- **Story**: `[enabler]` Emit one additional segment per sheet naming the sheet, its columns (in order), each column's inferred type, its total row count, and its first three data rows verbatim, so `document_search` can surface "what does this workbook contain" without reading every row window. Unblocks EP-4's sheet-resolution stories via a discoverable, human-legible sheet identity.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-201, US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a sheet, when it is extracted, then exactly one schema-card segment is produced for it, distinguishable in content from its row windows.
  - Given the schema card's column-type inference, when it runs, then it examines a bounded sample of each column's non-empty cells and infers one of `Text`/`Number`/`Date`/`Boolean`, defaulting to `Text` on a mixed or empty column.
  - Given a sheet with fewer than three data rows, when its schema card is built, then it includes however many rows actually exist rather than padding or failing.
  - Given the same column-type inference, when EP-3 persists the sheet's columns, then it reuses this story's inference rather than running a second, possibly disagreeing pass.

#### US-204: `[enabler]` Sheet-aware citation

- **Story**: `[enabler]` Change `DocumentRetrievalService`'s citation construction (`DocumentRetrievalService.cs:608-617`) so a chunk originating from a spreadsheet cites its sheet by name rather than reusing the `"{fileName} p.{page}"` format, which currently reads `budget.xlsx p.3` — a page number a sheet index is not.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-201, US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a passage from a spreadsheet-origin chunk, when its citation is built, then it names the sheet (for example `"budget.xlsx — Regional Revenue"`) rather than `"budget.xlsx p.3"`.
  - Given a passage from a PDF or Word document, when its citation is built, then it is byte-for-byte unchanged from today — this story touches only the spreadsheet branch.
  - Given `retrieval.md` §4.3's own documented rule ("the citation is the match's page, not the run's"), when a spreadsheet passage is widened by a neighbouring chunk from a different sheet (which cannot happen, since neighbour expansion never crosses a document's own chunk index space in a way that mixes sheets — confirmed by this story's own test), then the citation still names the best-matching chunk's own sheet.

### EP-3: Sheet schema & cell storage

#### US-301: `[enabler]` Add the sheet and column tables

- **Story**: `[enabler]` Add `BaseDocumentSheet`/`Core.ConversationDocumentSheet`/`Core.ProjectDocumentSheet` and `BaseDocumentSheetColumn`/`Core.ConversationDocumentSheetColumn`/`Core.ProjectDocumentSheetColumn`, plus the new `SheetColumnType` enum (`Text = 1, Number = 2, Date = 3, Boolean = 4`), mirroring `BaseDocumentSummary`'s split. Unblocks US-302, US-304.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given the two sheet tables, when they are configured, then each carries a unique index on **(owning document id, `SheetIndex`)**, filtered `[DateDeactivated] IS NULL` — not a one-per-document unique index, since a document can hold many sheets.
  - Given the two column tables, when they are configured, then each carries a unique index on **(sheet id, `ColumnIndex`)**, filtered the same way.
  - Given `SheetColumnType`, when it is configured, then it uses `HasConversion<int>()`, numbered from 1, following `JobStatus`'s own append-only convention.
  - Given every foreign key these four tables add, when the model builds, then `DeleteBehavior.NoAction` applies without any per-configuration opt-in, via the existing global loop in `OnModelCreating`.

#### US-302: `[enabler]` Add the row table with its native `json` cells column

- **Story**: `[enabler]` Add `BaseDocumentSheetRow`/`Core.ConversationDocumentSheetRow`/`Core.ProjectDocumentSheetRow`, whose `Cells` property is a `string` explicitly configured `.HasColumnType("json")`, holding one hand-serialized JSON object per row keyed by column name. Ship the one migration covering all six tables from this epic — the **fifteenth**, following `20260825224934_AddDocumentSummaries`. Unblocks US-303, US-304, EP-4.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-301
- **Status**: Not started
- **Acceptance criteria**:
  - Given the row table, when it is configured, then it carries a unique index on **(sheet id, `RowIndex`)**, filtered `[DateDeactivated] IS NULL`, and `Cells` is never part of any index key — supporting `sheet_query`'s lookups only ever happens through the sheet/column ordinal columns.
  - Given the migration, when it is generated against SQL Server 2025 at compatibility level 170, then `Cells` materializes as a native `json`-typed column, not `nvarchar(max)`.
  - Given the `json` type's preview status on SQL Server 2025 (GA only on Azure SQL Database/MI under the 2025 update policy), when this is deployed to a non-Azure-SQL SQL Server 2025 instance, then the risk is documented in this story's own implementation notes, with `HasColumnType("nvarchar(max)")` plus an `ISJSON` check constraint named as the fallback, scoped to `Cells` alone.
  - Given a row whose sheet has `ColumnCount` columns, when its `Cells` JSON is built, then it carries exactly that many properties — always far below the 65,535-property and 32K-unique-key SQL Server JSON caps, by construction rather than by a runtime check.
  - Given the migration, when it is reviewed, then it adds only these six tables — no existing table is touched.

#### US-303: `[enabler]` Adapt the SQLite fixture for the `json` column

- **Story**: `[enabler]` Confirm, with a new unit test, whether `SqliteRowVersionModelCustomizer`'s existing unconditional `property.SetColumnType(null)` pass is sufficient to load `Cells` on SQLite (§6 finding 4's expectation), and if it is not, remove the property the same way `SqlVector<T>` properties are removed today, with the choice recorded in a comment beside the customizer. Unblocks US-304 and every EP-4 unit test that needs the model to load on SQLite at all.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given the full `EnterpriseGptDbContext` model, when it is loaded via `SqliteDbContextFixture` after this story, then it succeeds with no exception, and every existing unit test suite that already depends on that fixture continues to pass unmodified.
  - Given a `ConversationDocumentSheetRow` inserted through the SQLite fixture, when its `Cells` value is round-tripped, then the resulting text is either the exact JSON string written (if the column type override was simply neutralized) or the property is confirmed absent from the SQLite model (if removal was necessary) — whichever the test proves, not whichever was assumed going in.
  - Given the outcome either way, when it is recorded, then a comment beside `SqliteRowVersionModelCustomizer` states which of the two happened and why, so a future engineer adding another `json` column does not have to re-derive the answer.

#### US-304: `[enabler]` Persist sheets, columns, and rows inside the existing ingestion save

- **Story**: `[enabler]` Add `ISheetStructureExtractor : IDocumentTextExtractor` with `Task<SheetExtractionResult> ExtractSheetsAsync(...)`, implemented by both new extractors from a single workbook parse, returning both text segments and structured `SheetStructureDto`s. Extend `DocumentService`'s ingestion path to type-check `is ISheetStructureExtractor`, and when true, attach the resulting sheet/column/row entities to the same `ConversationDocument`/`ProjectDocument` graph before the existing single `SaveChangesAsync` call (`DocumentService.cs:312-313`/`389-390`). Unblocks EP-4 entirely and US-501.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-201, US-202, US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given a `.xlsx` or `.csv` upload, when ingestion completes, then the document's sheet, column, and row rows exist in the database alongside its chunk rows, all committed by the **same** `SaveChangesAsync` call — a test asserts no separate save happens for the structured data.
  - Given any other extractor (`.pdf`, `.docx`, `.pptx`, `.md`, `.txt`), when it is used, then the `is ISheetStructureExtractor` check is false and the ingestion path is byte-for-byte unchanged from today.
  - Given an ingestion failure after the blob is written but before the save commits, when the existing `TryDeleteBlobAsync` cleanup runs, then it behaves identically regardless of whether structured sheet data was part of the failed graph.
  - Given the post-save parent-still-active recheck (`DocumentService.cs:315-329`), when a conversation is deactivated mid-ingestion, then the sheet/column/row rows are covered by the same set-based deactivation the chunk rows already receive, or a follow-up note explains why they need their own.

### EP-4: The `sheet_query` lookup

#### US-401: `[enabler]` Resolve a sheet and column by name, refusing what is unknown

- **Story**: `[enabler]` Add a sheet-matching helper analogous to `DocumentRetrievalService.MatchByName`, resolving a model-supplied `sheetName` (or, when omitted, the sole sheet across the turn's scope if exactly one exists) against the caller's own `ConversationDocumentSheet`/`ProjectDocumentSheet` rows, and a `columnName` against that sheet's own `SheetColumn` rows — both parameterized lookups. An unresolved or ambiguous name refuses by name, listing what is actually available, mirroring `document_summarize`'s own refusal pattern. Unblocks US-402.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-304
- **Status**: Not started
- **Acceptance criteria**:
  - Given a `sheetName` that matches no sheet in scope, when resolution runs, then it refuses, naming the sheets that are actually available — no query is attempted against a guess.
  - Given a `sheetName` omitted with more than one sheet in scope, when resolution runs, then it refuses, naming the available sheets, rather than picking one arbitrarily.
  - Given a `columnName` that does not exist on the resolved sheet, when resolution runs, then it refuses, naming that sheet's actual columns.
  - Given a sheet outside the caller's `DocumentRetrievalScope` (another user's conversation, or a project the caller does not belong to), when it is named, then it cannot be resolved at all — the same scope `document_search`/`document_summarize` already close over.

#### US-402: `[enabler]` Build the parameterized, no-raw-SQL query

- **Story**: `[enabler]` Add `SheetQuerySql` (`Enterprise.Gpt.Service/Tool/SheetQuerySql.cs`), building the aggregate/filter T-SQL from a resolved sheet and column set: every JSON path is built only from an already-validated `ColumnName`, passed as a bound `@path` parameter to `JSON_VALUE`/`OPENJSON`; the `RETURNING` type is chosen by a closed C# `switch` over the column's `InferredType`, selecting one of a small, fixed number of pre-authored parameterized command texts; every literal comparison value is bound as an ordinary parameter; results are capped by `TOP (@n)`. Unblocks US-403, US-404.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-401
- **Status**: Not started
- **Acceptance criteria**:
  - Given every command text this class can produce, when `SheetQuerySqlTests` inspects them, then none contains a literal, non-parameterized value or a request-derived path fragment — mirroring `DocumentRetrievalSqlTests`' own "no literal values in any generated statement" assertion for `document_search`.
  - Given a `Boolean`-typed column, when a comparison is built against it, then it compares as text (`'true'`/`'false'`) rather than `RETURNING bit`, since `bit` is not in `JSON_VALUE`'s supported `RETURNING` type list.
  - Given `SheetQuery:MaxResultRows` (default 50) and `:MaxGroups` (default 200), when a query would exceed either, then the generated statement caps it via `TOP (@n)` rather than returning an unbounded result.
  - Given this query path, when it is tested, then extraction/chunking-level tests run normally on SQLite, but every test that actually executes `JSON_VALUE`/`OPENJSON` SQL runs only in `Enterprise.Gpt.Integration.Test` against Testcontainers SQL Server 2025 — matching the same integration-only posture `retrieval.md` §12 already documents for anything touching the vector column, stated here as a known, accepted testing boundary rather than discovered later.
  - Given `SheetQuery:TimeoutSeconds` (default 10), when a query would run longer, then it is cancelled and the tool reports a bounded, sanitized failure rather than hanging the turn.

#### US-403: The model can aggregate a column, optionally grouped by another

- **Story**: As a chat user, I want to ask a computed question about a spreadsheet — a total, an average, a count, optionally broken down by another column — and get an exact answer, so that I do not have to open the file and compute it myself.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-402
- **Status**: Not started
- **Acceptance criteria**:
  - Given a sum, average, min, max, or count request over a resolved column, when `sheet_query` runs, then it returns the exact value computed directly from the stored rows.
  - Given the same request with a `groupBy` column supplied, when it runs, then it returns one row per distinct group value, each with its own aggregate, capped at `SheetQuery:MaxGroups`.
  - Given the fixed 20-query benchmark this PRD's success criteria reference, when it is run against a seeded workbook, then every result matches its hand-computed expected value exactly.
  - Given a `count` operation, when no value column is supplied, then it succeeds — `count` is the one operation that does not require a value column.

#### US-404: The model can filter and list matching rows

- **Story**: As a chat user, I want to ask which rows match a condition — not just a computed total — so that I can see the actual records behind an answer.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-402
- **Status**: Not started
- **Acceptance criteria**:
  - Given one or more filter criteria (up to `SheetQuery:MaxFilters`), when `sheet_query` runs with `operation: filter`, then it returns the matching rows' cell data, capped at `SheetQuery:MaxResultRows`.
  - Given more filters than `SheetQuery:MaxFilters` are supplied, when the tool is called, then it refuses with a message naming the limit rather than silently dropping filters.
  - Given a `contains` comparator against a `Text`-typed column, when it runs, then it matches a substring, mirroring `document_search`'s own lexical-pass semantics for identifiers.
  - Given zero matching rows, when the tool runs, then it returns an explicit empty result the model can relay, not an error.

#### US-405: `[enabler]` Attach `sheet_query` beside `document_search`

- **Story**: `[enabler]` Attach `sheet_query` in `ConversationService.CreateChatOptionsAsync`, copying `attachSummaryTool`'s own gate ladder: gated on `SheetQuery:Enabled`, the turn's document scope being non-empty, the selected model supporting tools, standing down if `attachDocumentTool` is false (mirroring why `document_summarize` stands down the same way), and standing down on a name collision with an already-selected MCP tool. Unblocks EP-5's flag story.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-403
- **Status**: Not started
- **Acceptance criteria**:
  - Given `SheetQuery:Enabled = false`, when a turn runs, then `sheet_query` is never attached, and the model has no way to discover it.
  - Given `SheetQuery:Enabled = true` and a conversation scope with no documents, when a turn runs, then `sheet_query` is not attached — matching `document_search`'s own precedent that an empty scope offers nothing to query.
  - Given `document_search` itself stood down (a model without tool support, or an MCP collision on `document_search`'s own name), when the gate ladder runs, then `sheet_query` stands down too, exactly as `document_summarize` already does relative to `document_search`.
  - Given an MCP server whose sanitized name collides with `sheet_query`, when it is selected for a turn, then `sheet_query` stands down for that turn and a warning is logged, mirroring the existing collision handling for the other two tools.

### EP-5: Scale, safety & governance

#### US-501: `[enabler]` Cap rows and columns accepted per sheet

- **Story**: `[enabler]` Enforce `Sheets:MaxRowsPerSheet` (default 20,000) and `Sheets:MaxColumnsPerSheet` (default 200) inside both new extractors, refusing a sheet that exceeds either as a `ValidationException` before any row is returned for chunking or persistence. Unblocks nothing further, but must land before this feature is enabled for real users.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-201, US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a sheet with exactly `MaxRowsPerSheet` rows, when it is uploaded, then it succeeds.
  - Given a sheet with `MaxRowsPerSheet + 1` rows, when it is uploaded, then the upload is refused with a message naming the limit, before any bytes are persisted for that sheet.
  - Given a sheet with more than `MaxColumnsPerSheet` columns, when it is uploaded, then it is refused the same way.
  - Given a workbook where only one of several sheets exceeds a ceiling, when it is uploaded, then the whole upload is refused rather than silently dropping the offending sheet — a partially ingested workbook would make `sheet_query`'s "what's in this workbook" answer wrong.

#### US-502: Re-tune retrieval defaults for row-window corpora

- **Story**: As an operator, I want confidence that `document_search`'s existing distance and fusion defaults still work once spreadsheet corpora exist, so that a header-repeated row-window chunk is neither drowned out nor over-favored relative to prose chunks.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-201, US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a benchmark corpus mixing prose documents and row-window-chunked spreadsheets, when retrieval is run against a set of representative queries, then the current `MaxDistance` (0.62) and fusion weights are confirmed adequate, or a specific, justified adjustment is proposed with before/after precision figures.
  - Given the repeated header text across a sheet's row windows, when the corpus's distance distribution is measured, then any systematic skew it introduces is documented, whether or not it changes the default.
  - Given the outcome, when it is recorded, then it updates `Documents:Chunking`/retrieval configuration only if a change is justified — this story does not mandate a change, only a validated answer.

#### US-503: `[enabler]` Emit sheet ingestion and `sheet_query` telemetry

- **Story**: `[enabler]` Add spans/metrics through the existing OpenTelemetry registration for sheet extraction (row count, column count, duration) and for `sheet_query` calls (operation, outcome, duration), with no cell content, sheet name, or document identity in any tag. Unblocks nothing; supports FR-23 and operational visibility.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-304, US-405
- **Status**: Not started
- **Acceptance criteria**:
  - Given a spreadsheet ingestion, when it completes, then a metric records its row count, column count, and duration, tagged only with the deployment/document-type shape existing metrics already use.
  - Given a `sheet_query` call, when it completes, then a metric records its operation and outcome (success, refused-by-name, timed-out, error), with no sheet name, column name, or cell value in any tag.
  - Given the existing `ChatMetrics` meter, when these new instruments are added, then they ride the same meter rather than introducing a second one.

#### US-504: `[enabler]` Feature-flag `sheet_query` with a documented rollback

- **Story**: `[enabler]` Gate `sheet_query`'s tool attachment behind `SheetQuery:Enabled`, defaulting `false` everywhere including development, matching `Summarization:Enabled`'s own precedent. Upload/extraction/chunking/storage for `.xlsx`/`.csv` ship unflagged, the same way `document_search` itself has no kill switch.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-405
- **Status**: Not started
- **Acceptance criteria**:
  - Given `SheetQuery:Enabled = false`, when the app starts and runs, then spreadsheet upload, extraction, chunking, and `document_search` over spreadsheet content all work normally — only `sheet_query`'s tool attachment is affected.
  - Given the flag is flipped to `true` and back to `false`, when this is exercised as a rehearsed rollback, then no redeploy is required and no already-ingested data is affected either way.
  - Given the flag's default, when a fresh environment starts with no explicit configuration, then `SheetQuery:Enabled` reads `false`.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **Phase 1 — the format exists** | EP-1 in full (US-101–US-103) | ~1 week |
| **Phase 2 — it is searchable and storable** | EP-2 in full (US-201–US-204); EP-3 in full (US-301–US-304), which needs only EP-1 and can run in parallel with the back half of EP-2 | ~2 weeks |
| **Phase 3 — the deterministic lookup** | EP-4 in full (US-401–US-405) | ~2 weeks |
| **Phase 4 — governance and switch-on** | EP-5 in full (US-501–US-504) | ~0.5 week |

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| SQL Server's `json` type is still preview on non-Azure-SQL SQL Server 2025 | US-302 names `nvarchar(max)` + `ISJSON` as a scoped, single-column fallback that changes nothing else in the schema |
| The header-repetition chunking strategy degrades under the chunker's own overlap/fallback mechanics for an oversized window | §6 finding 2 states the limitation explicitly; US-201/US-202's 95%-threshold acceptance criteria measure it rather than assume perfection |
| `MaxDistance`'s single global threshold no longer fits once row-window corpora exist | US-502 validates before this ships broadly, rather than discovering the regression in production |
| `sheet_query` becomes a raw-SQL-injection surface through careless implementation | US-402's acceptance criteria are enforced by a pinned test (`SheetQuerySqlTests`) mirroring `DocumentRetrievalSqlTests`' own "no literal values" assertion — a build-breaking regression guard, not a one-time review |
| A pathologically large workbook is uploaded before ceilings ship | US-501 lands in the same phase EP-3's persistence path lands, not deferred to the end |
| SQLite cannot load the `json` column at all, blocking every other unit test in the suite | US-303 is scheduled immediately after the row table exists, before EP-4 needs to write any unit test against it |

**Rollout & rollback.** Upload, extraction, chunking, and storage for `.xlsx`/`.csv` ship unflagged once accepted — the same posture `document_search` itself has always had, and consistent with there being no client-side kill switch needed (§6 finding 1). `sheet_query`'s own tool attachment sits behind `SheetQuery:Enabled`, defaulting off, exercised as a real rollback rehearsal in US-504 before general availability. The one schema change this PRD ships — the fifteenth migration, adding six additive tables — means rolling the **code** back leaves the database readable; nothing here alters or drops an existing table.

## 9. Assumptions & open questions

**Assumptions.** Each is a guess a reviewer can veto.

- `Sylvan.Data.Csv` is the chosen CSV dependency over a hand-rolled RFC 4180 reader, on correctness grounds (§6, "The package `.csv` needs"). A reviewer preferring CsvHelper or a different library should say so before US-102 starts; the acceptance criteria do not name the library, only its observable behavior.
- Row-window sizing targets 75% of the chunker's `MaxTokens` (`Sheets:RowWindow:TargetTokenFraction`) and caps at 50 rows (`Sheets:RowWindow:MaxRows`) — both are proposed defaults, not measured against a real corpus; US-502's benchmark is the first real validation either number gets.
- `Sheets:MaxRowsPerSheet` (20,000) and `:MaxColumnsPerSheet` (200) are proposed operational ceilings, chosen to sit comfortably under every SQL Server JSON cap while still covering the overwhelming majority of real-world business spreadsheets — not derived from a usage study, since none exists yet for a feature that has not shipped.
- `SheetQuery:MaxResultRows` (50), `:MaxGroups` (200), `:MaxFilters` (5), and `:TimeoutSeconds` (10) are proposed defaults mirroring the scale of `DocumentRetrievalSql`'s own caps (`MaxResults` 8, `CandidateCount` 40) scaled up for a tool that returns structured rows rather than prose passages, not measured against production load.
- `Cells`'s `.HasColumnType("json")` override is expected to be neutralized by `SqliteRowVersionModelCustomizer`'s existing unconditional `SetColumnType(null)` pass, based on Microsoft Learn's documented behavior of that API — but US-303 treats this as a hypothesis a test proves, not a certainty this PRD asserts.
- The exact sheet-aware citation format (`"{fileName} — {sheetName}"`) is a proposal; US-204's acceptance criteria require a sheet-distinguishable citation, not that specific string.
- No new permission id is introduced; `sheet_query` is gated exactly as `document_search` already is. A product decision to gate spreadsheet upload or `sheet_query` behind a dedicated permission would change §3 and FR-20/US-405, not the rest of this PRD.
- Numeric benchmark targets (the 20-query `sheet_query` benchmark, the 95% header-presence threshold) are proposed here for veto, not supplied by the original invocation.

**Open questions.**

- **Should `sheet_query` support more than one `groupBy` column in a future revision?** This PRD deliberately scopes v1 to a single group-by dimension; a genuine two-dimensional pivot request would need this reopened — *product owner, if usage data shows single-dimension grouping is insufficient*.
- **Does the eventual `.xlsm` decision (a non-goal here) want to reuse `SpreadsheetTextExtractor`'s row-window/schema-card mechanism once a macro-safety policy exists?** Left open rather than pre-designed against, since the policy itself does not exist yet — *product owner, when `.xlsm` is scoped*.
- **Should sheet ingestion and `sheet_query` share `document_search`'s existing rate/cost governance surfaces, or need their own?** `sheet_query` costs no model call, so the existing per-turn tool-iteration bound (`MaximumIterationsPerRequest = 5`) already bounds it; this is flagged only in case operational experience says otherwise — *operator, post-launch*.
