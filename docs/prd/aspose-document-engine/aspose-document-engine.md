# PRD: Aspose Document Engine

## 1. Overview

**Problem.** Enterprise GPT reads and writes Office documents through toolchains that were each chosen for one format family and cannot be compared against an alternative without replacing them outright: `.pdf`/`.doc`/`.docx` extraction goes through remote, per-page-billed Azure AI Document Intelligence; `.pptx`/`.xlsx`/`.csv` extraction is a local `DocumentFormat.OpenXml`/`CsvHelper` parse; conversation export writes `.docx` by hand with `DocumentFormat.OpenXml` and `.pdf` with PDFsharp-MigraDoc, which cannot render a page at all on a deployment that ships no font. An Aspose.Total licence is now available, and the product owner wants a second, licensed implementation the application can run side by side with today's — comparable on real documents, toggled per format by configuration — before any decision is made to prefer one over the other. Nothing in the codebase supports running two engines for the same format today: `DocumentTextExtractorFactory`'s constructor throws `InvalidOperationException` the moment two extractors claim one extension, and `ExportRendererRegistration` registers exactly one renderer per `ConversationExportFormats` member.

**Solution.** Give every format two candidate implementations but wire only one into dependency injection, chosen at startup from a typed per-format engine map — `Documents:Extraction:Engines` for ingestion, `Export:Engines` for conversation export — so a bad choice fails app start rather than a request, exploiting the same constructor guard that already makes the single-extractor world safe. Aspose extraction ships for `.pptx`, `.xlsx`, and `.csv` only; `.pdf`/`.doc`/`.docx` text extraction stays on Azure AI Document Intelligence, because Aspose cannot OCR a scanned page or an image-bearing Word document and this deployment is not willing to trade that fidelity away. The Aspose spreadsheet and presentation extractors reuse `SheetAssembler`/`SheetSegmentBuilder` verbatim, so a row window, a schema card, and a citation are the identical code regardless of which engine parsed the file — only the front-end that turns bytes into cell and slide data changes. Aspose.Words becomes a second `docx`/`pdf` export path, composed from one new print-oriented HTML template rather than the block model the current renderers walk, with the same refusal to fetch a remote resource that the current renderers already apply to images. A three-product licence (`Aspose.Total.NET.lic`) is applied once at composition from an embedded resource that is never committed, and selecting an Aspose engine anywhere without a successfully applied licence fails startup rather than running — silently or otherwise — in Aspose's evaluation mode, which contaminates extracted text with watermark content rather than merely watermarking a rendered page.

**Success criteria.**

- **Toggle correctness.** 100% of engine-map misconfigurations — an extension key that is not a `FileExtensions` member, an engine value outside `{Legacy, Aspose}`, an extension named with no Aspose implementation, or an `Aspose` selection with no successfully applied licence — fail application startup rather than reaching a request, measured by one startup-validation test per case in `Enterprise.Gpt.Unit.Test`.
- **Segment parity.** 100% of the `WorkbookBuilder`/`PresentationBuilder` fixture corpus the parity harness runs produces segment-for-segment identical output — text, `SourceNumber`, schema-card content, row-window boundaries, speaker notes — between the `Legacy` and `Aspose` engines for every extension where both are attempted, measured by `EngineParityTests` failing on any diff.
- **Licence integrity.** 0 Aspose-authored segments or exports contain Aspose evaluation-mode watermark text in any test run, and 0 CI runs report a passing result for an Aspose-dependent test executed without a successfully applied licence, measured by the `License.IsLicensed` assertion each such test carries and by the CI job described in §5 that confirms the trait's tests actually ran on `push` to `master` and on same-repo pull requests.
- **No remote fetch from export.** 0 outbound HTTP requests originate from `HtmlLoadOptions.ResourceLoadingCallback` while composing a `docx`/`pdf` export through the Aspose path, for any input the template or a message's rendered HTML could carry, measured by `AsposeExportSecurityTests` asserting the callback refuses every URI it is offered.
- **Capability measured, not assumed.** 100% of EP-0's four packaging measurements — publish output size delta, deployed zip size delta, cold-start time delta, and CI restore-time delta, each against the current baseline on the Windows App Service target — are recorded in the PRD's own tracking before any Aspose engine ships selected in a committed `appsettings.json`.

## 2. Goals & non-goals

**Goals.**

- Give the platform a second, licensed extraction implementation for `.pptx`, `.xlsx`, and `.csv`, selectable per format by configuration alone, with no runtime toggle and no code change to switch.
- Give the platform a second export implementation for `docx` and `pdf`, built on Aspose.Words from one new template, selectable the same way and composing with the export formats a deployment can already withdraw.
- Make an Aspose-produced segment indistinguishable in shape from the segment the existing pipeline already produces for the same extension, by reusing `SheetAssembler`/`SheetSegmentBuilder` verbatim rather than reimplementing schema cards, row windows, or column-type inference a second time.
- Prove the three-product licence bootstrap works — three `SetLicense` calls from one embedded resource, never committed — before any code depends on it, and make an unlicensed `Aspose` selection fail loudly at startup rather than degrade silently into evaluation-mode output.
- Measure the real cost of a roughly 227 MB set of native-backed packages — publish size, deployed zip size, cold start, CI restore time — against the Windows App Service target this application actually deploys to, before enabling any of it by default.
- Keep the licence file out of git and out of every fork pull request's ability to exercise an Aspose engine unlicensed, by design rather than by convention.
- Leave `.xlsx`/`.csv` ingestion and `docx`/`pdf` export reachable through Aspose without disturbing what a deployment gets today: the shipped default is `Legacy` everywhere, unchanged behavior for anyone who does not opt in.

**Non-goals.**

- **Word and PDF text extraction via Aspose.** `.pdf`, `.doc`, and `.docx` stay on Azure AI Document Intelligence. Aspose.Words and Aspose.Cells cannot perform OCR on a scanned page or an image-bearing Word document, and Document Intelligence's OCR fidelity for exactly those files is not a trade this PRD is willing to make. The Aspose extraction surface in this document is `.pptx`, `.xlsx`, and `.csv` only.
- **The `Aspose.PDF` package.** Aspose.Words renders PDF itself through `SaveFormat.Pdf`, so the 197 MB `Aspose.PDF` package buys this PRD nothing. Excluding it is a deliberate scoping decision recorded here, not an oversight discovered later.
- **The File Agent's Python-sandbox conversions.** `Service/Agents/` and `conversion-matrix.json` (`docs/prd/file-agent/file-agent.md`) are a different conversion surface — model-directed, running inside a hosted code interpreter — entirely unaffected by which engine ingests or exports a conversation document.
- **Markdown export moving to Aspose.** `ConversationExportFormats.Markdown` is documented (`Export/IConversationExportRenderer.cs`) as carrying a message's text exactly as it was authored; round-tripping it through Aspose.Words' object model would be a fidelity regression for a format whose entire value is fidelity to the source. The original request named Markdown among the formats to compare — this PRD explicitly declines, because there is nothing for a second engine to improve here and something for it to lose.
- **A runtime or administrator-facing toggle.** Both engine maps are resolved once at startup into which types are registered in DI. Changing either needs a restart, exactly like every other per-format decision `DocumentTextExtractorFactory`/`ExportRendererRegistration` already make at composition. There is no admin screen, no per-conversation setting, and no per-request override.
- **`.xls`/`.xlsm` ingestion and Aspose Word/PDF extraction as anything other than an optional, defaulted-off extension.** Both are in scope for EP-6, shipped registered but never selected by the committed engine map, so the platform's OCR-first default for Word/PDF is not implicitly revisited by shipping this PRD.
- **A deploy-time (CD) equivalent of the CI licence-materialization step.** `api-cd.yml` does not exist yet — only `api-ci.yml` and `ui-ci.yml` exist under `.github/workflows/`; the three CD workflows are specified but not built in `docs/prd/azure-infrastructure/azure-infrastructure.md`. This PRD's licence handling covers CI only; the deployed App Service's own licence delivery is a stated dependency on that PRD's CD epic, recorded in §8 rather than designed here.
- **Updating `docs/` or `CHANGELOG.md`.** A PRD is a pre-implementation artifact; the SE Technical Writer flow handles both after implementation and review.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee uploading documents and exporting conversations. Sees no change of any kind from this PRD — the same upload flow, the same download menu, the same file shapes come back regardless of which engine produced them.
- **Administrator**: holds `PermissionIds.Administrator`. Gains no new screen and no new setting in the model or MCP catalog UI; the engine maps are deployment configuration, not an admin-managed resource.
- **Operator**: runs the deployment. Owns `Documents:Extraction:Engines`, `Export:Engines`, the `Aspose.Total.NET.lic` repository secret, and the rollback (flip either map back to `Legacy` and restart).
- **Backend engineer**: owns every epic in this document, in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`.

**Role-based access.**

- **Anonymous**: no access. Every route this feature could touch — upload, download, export — already sits inside a group carrying `.RequireAuthorization()`, and this PRD adds no route of its own.
- **Chat user**: uploads and exports exactly as today; ownership and authorization are unchanged, because which extractor or renderer ran is an internal implementation detail carried on no DTO and exposed through no header.
- **Administrator**: no new administrative capability. Neither engine map is readable or writable through any HTTP surface — both are `IConfiguration`, resolved once at startup, with no corresponding endpoint.
- **Operator**: the only actor who can change which engine runs for a format (editing configuration and restarting) or who can rotate the licence secret. Neither action is reachable from any HTTP route.
- **The licence file itself**: never served. It is an embedded assembly resource consumed only by `AsposeLicense`'s `SetLicense` calls at composition; no endpoint reads it, downloads it, or reflects its contents into a response.

No new permission id is introduced anywhere in this PRD.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | `Aspose.Words`, `Aspose.Cells`, and `Aspose.Slides.NET` 26.8.0 are referenced in `Enterprise.Gpt.Service.csproj`, each with a house-style justification comment; no `Aspose.PDF` reference is added | P0 | EP-0 |
| FR-2 | A capability spike proves all three `SetLicense` calls succeed against one embedded `.lic` resource and reports `License.IsLicensed` per product, before any production code depends on it | P0 | EP-0 |
| FR-3 | `Aspose.Total.NET.lic` is never committed; `.gitignore` excludes it beside the existing secrets block; `api-ci.yml` materializes it from a repository secret before any Aspose-dependent build or test step, and does nothing when the secret is unavailable (a fork pull request) | P0 | EP-0 |
| FR-4 | Every Aspose-dependent test carries a shared trait and self-skips, naming the missing licence, rather than running unlicensed or reporting a false pass; `api-ci.yml` asserts the trait's tests actually executed on `push` to `master` and on same-repo pull requests | P0 | EP-0 |
| FR-5 | Publish output size, deployed zip size, cold-start time, and CI restore-time deltas are measured against the Windows App Service target and recorded before any Aspose engine ships selected in a committed `appsettings.json` | P1 | EP-0 |
| FR-6 | The Windows App Service target's available font faces are inventoried against the print template's chosen faces before EP-4 ships | P1 | EP-0 |
| FR-7 | `Documents:Extraction` binds a per-extension engine map (`.pptx`, `.xlsx`, `.csv` → `Legacy`/`Aspose`); an absent or empty map resolves to `Legacy` for every extension | P0 | EP-1 |
| FR-8 | Startup validation rejects an extension key that is not a `FileExtensions` member, an engine value outside `{Legacy, Aspose}`, and an extension named with no Aspose implementation, each naming the full colon-delimited key | P0 | EP-1 |
| FR-9 | `AsposeLicense` applies all three `SetLicense` calls once at composition, before any registration can construct an Aspose object; selecting `Aspose` for a format whose product did not license successfully fails startup naming the config key | P0 | EP-1 |
| FR-10 | `ExtractionEngineRegistration` registers exactly one `IDocumentTextExtractor` per extension, resolved from the engine map, as a direct sibling of `ExportRendererRegistration`; `DocumentTextExtractorFactory`'s existing two-extractors-one-extension guard is the sole enforcement mechanism | P0 | EP-1 |
| FR-11 | The factory's startup log names the engine that won each extension | P1 | EP-1 |
| FR-12 | An Aspose `.xlsx` extractor implements `ISheetStructureExtractor`, reusing `SheetAssembler`/`SheetSegmentBuilder` verbatim so row windows, schema cards, column-type inference, and citation text are produced by the identical code the `Legacy` path already runs | P0 | EP-2 |
| FR-13 | The Aspose `.xlsx` extractor's `SourceNumber` is the workbook's own sheet order, matching `SpreadsheetTextExtractor`'s existing contract | P0 | EP-2 |
| FR-14 | An Aspose `.csv` extractor reuses `CsvTextExtractor`'s own delimiter-sniffing pass (comma/semicolon/tab) and its UTF-16 BOM handling ahead of `TxtLoadOptions.Separator`, since Aspose.Cells performs no delimiter auto-detection of its own | P0 | EP-2 |
| FR-15 | Every `Sheets:*` ceiling (`MaxRowsPerSheet`, `MaxRowsPerUpload`, `MaxColumnsPerSheet`, `MaxCharactersPerUpload`) is enforced while an Aspose `.xlsx` parse streams, via `LoadOptions.LoadFilter`/`MemorySetting`, with peak working set measured against Aspose.Cells' default whole-workbook materialization | P0 | EP-2 |
| FR-16 | A parity test harness runs both the `Legacy` and `Aspose` engines over the same `WorkbookBuilder` fixtures and asserts segment-for-segment equality, including sheet ordinal, schema-card content, and row-window boundaries | P0 | EP-2 |
| FR-17 | A malformed or password-protected spreadsheet fed to an Aspose extractor fails as `FluentValidation.ValidationException` → 400 through named exception filters, matching `PresentationTextExtractor`'s existing pattern | P1 | EP-2 |
| FR-18 | The Aspose `.csv` extractor is shown, by test, to keep the raw text of a stray quote in an unquoted field the way `CsvTextExtractor`'s cleared `BadDataFound` already does; if that tolerance is not reachable, `.csv` stays on `Legacy` permanently as a documented, supported outcome rather than a blocked one | P1 | EP-2 |
| FR-19 | An Aspose `.pptx` extractor implements `IDocumentTextExtractor`, preserving slide-ordinal `SourceNumber` from the presentation's own slide order and speaker notes text | P0 | EP-3 |
| FR-20 | A malformed or password-protected `.pptx` fed to the Aspose extractor fails as `ValidationException` → 400 | P1 | EP-3 |
| FR-21 | The parity harness (FR-16) is extended to `.pptx` using `PresentationBuilder` fixtures | P0 | EP-3 |
| FR-22 | A new print-oriented HTML template and `IExportHtmlComposer`, built from the existing `ConversationExportDocument` model, is authored in a Word-compatible CSS subset — tables, simple selectors, inline styles, no custom properties, no flex or grid | P0 | EP-4 |
| FR-23 | `HtmlLoadOptions.ResourceLoadingCallback` refuses every external URI when Aspose.Words loads the print template, so HTML import cannot reintroduce the SSRF the existing renderers deliberately avoid | P0 | EP-4 |
| FR-24 | `Export:Engines` binds a per-format engine map (`docx`, `pdf` → `Legacy`/`Aspose`), the same shape and `Legacy` default as extraction, composing with the existing `Export:DisabledFormats` | P0 | EP-4 |
| FR-25 | Aspose Word/PDF export renderers load the print template once per export and save it to the requested format | P0 | EP-4 |
| FR-26 | A PDF font substitution performed by Aspose.Words is surfaced through an `IWarningCallback` to telemetry, rather than degrading silently | P1 | EP-4 |
| FR-27 | `ConversationExportFormats.Markdown` never acquires an Aspose renderer, regardless of the engine map's contents | P1 | EP-4 |
| FR-28 | Per-extraction and per-export engine/outcome/duration metrics ride the existing `Enterprise.Gpt.Chat` meter, with no document content, sheet name, column name, or file name in any tag | P1 | EP-5 |
| FR-29 | `ExportAvailabilityLogger` reports which engine produced, or could not produce, each format | P1 | EP-5 |
| FR-30 | Rolling either engine map back to `Legacy` and restarting is the entire rollback: no migration, no schema change, no data change, and already-produced segments and exports are unaffected either way | P1 | EP-5 |
| FR-31 | Aspose Word, Aspose PDF-via-Aspose.Words, and Aspose `.xls`/`.xlsm` extractors ship registered but are never selected by the shipped engine map | P2 | EP-6 |

## 5. Technical considerations

**The load-bearing constraint the whole design leans on.** `DocumentTextExtractorFactory`'s constructor (`Enterprise.Gpt.Service/Extraction/IDocumentTextExtractorFactory.cs`) throws `InvalidOperationException` the moment two registered `IDocumentTextExtractor`s claim the same `FileExtensions` member. That guard is why a per-format engine map resolved *before* `AddSingleton<IDocumentTextExtractor, …>()` runs is the only workable toggle shape: registering both a `Legacy` and an `Aspose` extractor for `.xlsx` unconditionally would fail every app start, by design, the same way a genuine wiring mistake fails one today. `ExportRendererRegistration` (`Enterprise.Gpt.Api/Export/ExportRendererRegistration.cs`) makes the identical choice for export today by calling `Register<TRenderer>` for exactly one type per `ConversationExportFormats` member; this PRD's `ExtractionEngineRegistration` and the extended export registration both read a map first and then call the *one* registration each format resolves to — no dual registration, ever, for either surface.

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| The extraction extension point this PRD adds a chooser in front of | `Enterprise.Gpt.Api/Program.cs:784-795` — five `AddSingleton<IDocumentTextExtractor, …>()` registrations plus `IDocumentTextExtractorFactory`/`ITextChunker`, under the comment *"Registering an `IDocumentTextExtractor` is all it takes to support a new format"* |
| The guard that makes the toggle safe | `Enterprise.Gpt.Service/Extraction/IDocumentTextExtractorFactory.cs` — `DocumentTextExtractorFactory`'s constructor, the `InvalidOperationException` on a duplicate extension claim |
| The extraction contracts an Aspose extractor implements | `Enterprise.Gpt.Service/Extraction/IDocumentTextExtractor.cs` (`SupportedExtensions`, `ExtractAsync`), `Enterprise.Gpt.Service/Extraction/ISheetStructureExtractor.cs` (`ExtractSheetsAsync` returning `SheetExtractionResult`) |
| The runtime type-test that would silently disable sheet persistence if missed | `DocumentService.ExtractAsync` — `extractor is ISheetStructureExtractor sheetExtractor ? await ExtractSheetsAsync(...) : ...`; an `.xlsx`/`.csv` extractor that does not implement `ISheetStructureExtractor` produces text with no error anywhere, and no sheet rows, no schema card retrieval, and no `sheet_query` support |
| Shared segment-shaping code Aspose extractors must reuse, not reimplement | `Enterprise.Gpt.Service/Extraction/SheetAssembler.cs` (`internal static class SheetAssembler`, the schema card, header-repeating row windows, column-type inference, returns `internal sealed record SheetAssembly(Segments, Structure, CellCharacters)`) and `Enterprise.Gpt.Service/Extraction/SheetSegmentBuilder.cs` (`internal static class SheetSegmentBuilder`, makes `.xlsx` and `.csv` emit byte-identical segment text) |
| The template to read for the exception-filter and BOM/delimiter patterns to copy | `Enterprise.Gpt.Service/Extraction/PresentationTextExtractor.cs` (named exception filter → `ValidationException`, slide ordinal from `SlideIdList`, speaker notes from `NotesSlidePart`) and `Enterprise.Gpt.Service/Extraction/CsvTextExtractor.cs` (`ResolveDelimiter` sniffing `,`/`;`/`\t` by per-line hit count, `BadDataFound = null`, `StreamReader(..., detectEncodingFromByteOrderMarks: true)`) |
| Spreadsheet streaming ceilings an Aspose extractor must enforce the same way | `Enterprise.Gpt.Service/Settings/SheetOptions.cs` — `MaxRowsPerSheet` (20,000), `MaxRowsPerUpload` (50,000), `MaxColumnsPerSheet` (200), `MaxCharactersPerUpload` (8,000,000), enforced *while streaming* in `SpreadsheetTextExtractor.cs` because a workbook is a compressed archive whose uncompressed size the upload-size limit does not bound |
| Background concurrency an extractor is instantiated once and shares | `Enterprise.Gpt.Service/BackgroundJobs/BackgroundJobProcessor.cs:43-44` — `BackgroundJobs:MaxConcurrent`, defaulting to `Environment.ProcessorCount * 2` when unset or non-positive; every `IDocumentTextExtractor` is a singleton (`Program.cs:789-793`), so an Aspose extractor may hold no per-document Aspose object in an instance field |
| The upload validator whose accepted-extension set is derived, not hand-maintained | `Enterprise.Gpt.Service/Validators/UploadedFileValidator.cs` — `IsSupported`/`SupportedExtensionNames` come from `IDocumentTextExtractorFactory`, so this PRD changes no validator code; whichever engine is registered per extension is what the validator already advertises |
| The full extension surface, and why only eight of twenty-seven have extractors today | `Enterprise.Gpt.Dto/Enums/FileExtensions.cs` — 27 members; `.pdf`, `.doc`, `.docx`, `.pptx`, `.xlsx`, `.csv`, `.md`, `.txt` are the eight with an extractor today, `.xls`/`.xlsm` declared but unreadable (EP-6) |
| The export extension point this PRD adds a chooser in front of, and its direct template | `Enterprise.Gpt.Api/Export/ExportRendererRegistration.cs` — `internal static class`, `AddExportRenderers`, `Register<TRenderer>` per format, called from `Enterprise.Gpt.Api/Program.cs:744-746`; `GlobalFontSettings.FontResolver` is set here, at composition, exactly where `AsposeLicense`'s three `SetLicense` calls belong |
| The export contract every renderer implements | `Enterprise.Gpt.Service/Export/IConversationExportRenderer.cs` — `ConversationExportFormats { Html=1, Json=2, Markdown=3, Docx=4, Pdf=5 }`, `ConversationExport(byte[] Content, string ContentType, string FileName)`, `IConversationExportRenderer { Format, Render(document) }` |
| The reduced model every renderer, existing or new, consumes | `Enterprise.Gpt.Service/Export/ConversationExportDocument.cs` — `Header`, `Stored`, `Messages` (`ConversationExportMessage(Role, Label, Markdown, Html)`); carries no activity cards, no reasoning text, no unsaved partial answer |
| The wire tokens and the route | `Enterprise.Gpt.Service/Export/ConversationExportFormatNames.cs` (`Supported = ["html","json","md","docx","pdf"]`) and `Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs` — `GET api/conversations/{id:guid}/export?format=…`, handler `ExportConversationAsync`; `Export:Engines`'s keys are the same `docx`/`pdf` tokens, not the enum names |
| Existing export withdrawal mechanism this composes with | `Enterprise.Gpt.Service/Settings/ExportOptions.cs` — `DisabledFormats` (default `[]`), `Pdf.FontDirectory`; `Export:Engines` is added to this same class |
| Startup reporting to extend, not replace | `Enterprise.Gpt.Api/Export/ExportAvailabilityLogger.cs` — an `IHostedService` that logs which formats are available and why not, re-deriving the reason from `ExportRendererRegistration.IsEnabled` rather than being told it |
| The security precedent Aspose export must match, not regress | `docs/conversations/conversation-export.md` §5.2 — *"Embedding the image itself would mean the API making an outbound HTTP request to a URL that model output chose… a server-side request forgery surface with no legitimate purpose here."* `docx`/`pdf` today drop images and keep alt text for exactly this reason; loading an HTML template through Aspose.Words' HTML importer would reintroduce the identical class of request unless refused explicitly |
| Why PDF is unavailable in some deployments today, and what an Aspose path changes | `Enterprise.Gpt.Service/Export/Fonts/README.md` — PDFsharp's cross-platform build ships no fonts and reads none from the OS unless told to; `Export/Fonts/` ships no font binary, so a bare container has no usable face and the PDF renderer is simply never registered. Aspose.Words substitutes a font rather than throwing, which is a behavior this PRD must make observable (FR-26) rather than let stand as silent degradation |
| Options-binding and startup-validation conventions to follow | `Enterprise.Gpt.Api/Program.cs:488-524` — `AddOptions<T>().Bind(...).ValidateDataAnnotations().Validate(..., "message").ValidateOnStart()`; cross-field/nested validation lives here, not in attributes, because `ValidateDataAnnotations` does not reach a nested object (see the `SheetOptions` block at `:498-509`); every validation message names the full colon-delimited key |
| The "off by default" convention for a feature that spends something | `Enterprise.Gpt.Service/Settings/SummarizationOptions.cs`, `Settings/FileAgentOptions.cs` — `bool Enabled { get; set; }` with **no initializer**, off everywhere including development, proven by a `Settings/*OptionsTests.cs` asserting the shipped default; this PRD mirrors the same shape for the shipped `appsettings.json`'s engine maps (empty/absent = `Legacy` everywhere) rather than for a single flag |
| Enum-numbering convention for the new engine vocabulary | `Enterprise.Gpt.Common/Enums/SheetColumnType.cs`, `Enterprise.Gpt.Dto/Enums/JobStatus.cs` — numbered from 1, appended rather than renumbered; `DocumentEngines { Legacy = 1, Aspose = 2 }` follows the same convention and binds directly from a configuration string through `Dictionary<string, DocumentEngines>` |
| Test fixtures the parity harness runs against | `tests/Enterprise.Gpt.Unit.Test/TestInfrastructure/WorkbookBuilder.cs`, `PresentationBuilder.cs` — synthesize `.xlsx`/`.pptx` bytes in memory, including deliberately corrupt variants |
| The `.gitignore` block the licence file's entry joins | `.gitignore:67-79` — *"The application takes its secrets from Key Vault through `DefaultAzureCredential`… nothing below should ever exist in a clean checkout"* — `.env`, `.env.*`, `appsettings.*.local.json`, `*.pfx`, `*.snk`, `*.p12`, `secrets.json` |
| The CI workflow the licence-materialization and Aspose-test-ran-check land in | `.github/workflows/api-ci.yml` — `changes` → `unit`/`integration`, fail-open `if:` conditions, `permissions: contents: read`, no `secrets.*` reference anywhere today; `ui-ci.yml`'s standalone `contract` job (Andes contract drift, gated on `changes`, a restore plus a file comparison) is the closest existing shape for a job that exists to verify one narrow thing |
| The reasoning a new CI gate must not violate | `docs/ci/pull-request-checks.md` §3 — *"A job skipped by a conditional reports Success… `!= 'false'` means an absent answer runs the tests"*; the same reasoning applies to an Aspose-test-ran assertion: if it is written to pass when the tests were skipped, a broken licence secret hands out a green check over code nobody exercised licensed |

**GitHub Actions cannot expose a repository secret to a fork pull request's workflow run.** That is a platform constraint, not a configuration gap this PRD can close: on a fork PR there is no `Aspose.Total.NET.lic`, `AsposeLicense`'s `SetLicense` calls do not apply, and any test that exercises an Aspose engine either fails outright or — the failure mode this whole PRD exists to prevent — runs against Aspose's evaluation mode and asserts against watermark-contaminated output while reporting green. The mitigation is in EP-0 (US-003): every Aspose-dependent test carries a shared trait, checks `License.IsLicensed` for the product it exercises, and self-skips naming the missing licence rather than running unlicensed. Because a skipped test also reports success, that alone is not sufficient — `api-ci.yml`'s `unit` job additionally asserts the trait's tests actually *ran* (not skipped) on `push` to `master` and on a same-repo pull request, mirroring `docs/ci/pull-request-checks.md`'s own reasoning about a `changes` job that dies silently handing out a green check over untested code. On a fork PR, the assertion itself is skipped rather than failed — a contributor without repository access cannot be made to fail a check they have no way to satisfy — so the gate binds exactly where the codebase's own trust boundary already sits: `master` and same-repo pull requests.

**Data storage & privacy.** This PRD adds no entity, no table, and no migration. Every Aspose extractor produces the same `DocumentSegmentDto`/`SheetStructureDto` shapes the `Legacy` extractors already produce, persisted through the identical `DocumentService` save path; every Aspose export renderer produces the same `ConversationExport` record the `Legacy` renderers already produce, served through the identical `ExportConversationAsync` handler. Nothing about which engine ran is persisted anywhere — not on the document row, not on the export response — because nothing downstream needs to know, and recording it would be a fact that goes stale the moment an operator flips the map.

**Security.**

- **The one hard requirement: no export composed through Aspose.Words may fetch a remote resource.** `HtmlLoadOptions.ResourceLoadingCallback` is set to refuse every external URI the print template or a message's stored `htmlContent` could reference, before the callback is ever exercised against real input. This is the identical SSRF the current Word/PDF renderers already refuse by dropping images outright (`docs/conversations/conversation-export.md` §5.2) — reusing Aspose.Words' own HTML importer must not quietly reopen a door the platform closed on purpose. FR-23 and US-402 exist because of this paragraph alone, and it carries the same prominence `docs/prd/sheet-ingestion/sheet-ingestion.md` gives "no raw SQL, ever."
- **Unlicensed must never be silent.** Aspose.Words' evaluation mode injects watermark text into the document object itself — not only a rendered page — so an unlicensed `Aspose` selection would contaminate extracted text passed to embeddings and to the model, not merely watermark an export. `AsposeLicense`'s per-product `License.IsLicensed` check, enforced as a startup validation tied to the engine map, is what makes this unreachable rather than merely unlikely.
- **The licence file is a secret, handled as one.** `Aspose.Total.NET.lic` never lands in git; `.gitignore` excludes it beside `*.pfx`/`*.snk`/`*.p12`/`secrets.json`; CI materializes it from a repository secret that a fork pull request's workflow run cannot see. No application code logs its contents, its path beyond the fixed embedded-resource name, or anything derived from it.
- **No new authorization branch.** Every route this feature could touch is already authorized identically regardless of which engine produced the bytes on either side of it.

**Scalability & performance.**

- **Aspose.Cells materializes a whole workbook where `SpreadsheetTextExtractor` streams.** With `Documents:MaxFileSizeBytes` at 50 MB and `BackgroundJobs:MaxConcurrent` defaulting to `Environment.ProcessorCount * 2`, several large workbooks parsing concurrently under Aspose.Cells' default loading behavior is a real peak-working-set risk the `Legacy` path does not carry in the same shape. `LoadOptions.LoadFilter`/`MemorySetting` must be specified to bound this, and peak working set must be measured under realistic concurrency (EP-2, US-203) rather than assumed safe because the `Sheets:*` ceilings already bound the *output*.
- **Package weight is a real cost, not a rounding error.** `Aspose.Words`, `Aspose.Cells`, and `Aspose.Slides.NET` 26.8.0 total roughly 227 MB of nupkg, and SkiaSharp arrives as a new transitive native dependency through Aspose.Words. Publish output size, deployed zip size (`WEBSITE_RUN_FROM_PACKAGE = 1`), cold-start time, and CI restore time must each be measured against the current baseline before this ships enabled anywhere (EP-0, US-004) — not assumed acceptable because the licence works.
- **Windows App Service is the serving target; ubuntu is the asserting one.** `api-ci.yml`'s `unit`/`integration` jobs run on `ubuntu-latest`/`ubuntu-24.04`; the API deploys framework-dependent, with no `RuntimeIdentifier`, to `azurerm_windows_web_app` (`docs/prd/azure-infrastructure/azure-infrastructure.md`). Font availability for the Aspose PDF export path and any Aspose.Cells/Aspose.Words behavior that reads platform state must be verified against the Windows target — no test in this PRD may assert on a font or a platform-dependent behavior resolved only on the CI host.
- **Extraction concurrency is unchanged in shape.** An Aspose extractor is registered as a singleton exactly like its `Legacy` counterpart and must hold no per-document Aspose object (`Document`, `Workbook`, `Presentation`) in an instance field — those types are not documented as thread-safe, and the background job pipeline already runs several extractions concurrently on one process.

## 6. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-0 | Licensing, packaging & capability spike | Prove the three-product licence bootstrap; keep the licence out of git and out of fork PRs; measure package weight and cold start against the Windows target; inventory its fonts | P0 | L | — |
| EP-1 | The extraction engine seam | The per-extension engine map, its validation, the licence-gated registration, and startup reporting | P0 | M | EP-0 |
| EP-2 | Aspose spreadsheet & CSV extraction, with the parity harness | `.xlsx`/`.csv` read through Aspose.Cells, byte-identical in segment shape to today, proven by a harness | P0 | L | EP-1 |
| EP-3 | Aspose presentation extraction | `.pptx` read through Aspose.Slides, proven against the same harness | P0 | M | EP-1 |
| EP-4 | Template-driven export | The print template, its composer, the Aspose `docx`/`pdf` renderers, `Export:Engines`, the resource-loading refusal | P0 | L | EP-0 |
| EP-5 | Rollout, observability & rollback | Name the winning engine everywhere it matters, and prove the rollback is a config edit and a restart | P1 | M | EP-2, EP-3, EP-4 |
| EP-6 | Optional extensions | `.xls`/`.xlsm` and Aspose Word/PDF extractors, shipped but defaulted off | P2 | M | EP-1, EP-4 |

EP-0 gates everything else because two of its findings are irreversible surprises if discovered late: a licence that does not actually apply the way the vendor documents, and a package weight or cold-start cost the Windows App Service target cannot absorb. It follows the `docs/prd/file-agent/file-agent.md` EP-0 precedent of a provisioning and capability spike whose findings are confirmed, not assumed, before estimating the epics that depend on them — and here it additionally carries the licence-security work (keeping the `.lic` out of git, and keeping an unlicensed CI run from reporting a false pass), because both are properties of *how the licence is proven*, not of any later epic. EP-1 is the seam every other extraction epic plugs into, and is sized M rather than L because its four stories are genuinely small once the licence and the extractor contracts already exist. EP-2 and EP-3 can run in parallel once EP-1 closes — spreadsheet/CSV and presentation extraction share no file — but EP-2 is sized L because it carries both the higher-risk CSV parity question and the memory-safety work Aspose.Cells' materialization model demands, neither of which EP-3 needs. EP-4 depends only on EP-0 (the licence) rather than on EP-1 through EP-3, because export and extraction share no code path; it is sequenced after EP-1 in the epics table purely because extraction is the platform's primary document workflow, not because of a dependency edge. EP-5 closes last because naming an engine in telemetry and rehearsing a rollback are only worth doing against a feature that already does something. EP-6 is how the original "Word and PowerPoint with Aspose" instinct stays reachable by configuration without displacing the OCR-first default this PRD locks in for Word and PDF — the two Aspose extractors and the two missing spreadsheet extensions ship, registered, provably inert, so a future decision to reconsider Word/PDF extraction is a config edit rather than a new PRD.

### EP-0: Licensing, packaging & capability spike

#### US-001: `[enabler]` Prove the three-product licence bootstrap

- **Story**: `[enabler]` Stand up a throwaway console or skipped-integration-test harness that embeds a real `Aspose.Total.NET.lic` as an assembly resource, calls `Aspose.Words.License.SetLicense`, `Aspose.Cells.License.SetLicense`, and `Aspose.Slides.License.SetLicense` against it by short resource name, and reports each product's `License.IsLicensed`. Unblocks US-102 and every later Aspose-authored extractor or renderer, which must not be built against an unproven licensing mechanism.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started.
- **Acceptance criteria**:
  - Given one embedded `Aspose.Total.NET.lic` resource, when all three `SetLicense` calls run against it by short resource name, then `Aspose.Words.License.IsLicensed`, `Aspose.Cells.License.IsLicensed`, and `Aspose.Slides.License.IsLicensed` each report `true`.
  - Given no licence resource is present (the shape of a developer machine or a fork PR with no secret), when the same three calls are attempted, then each fails in a caught, logged way and every product's `IsLicensed` reports `false` — proving the "no licence, no crash" path as directly as the "licence, working" path.
  - Given a licensed `Aspose.Words.Document`, when a small document is created and saved to text, then no evaluation-mode watermark text is present in the output — the concrete evidence that licensing prevents the text-contamination failure mode, not merely a rendered-page watermark.
  - Given the harness completes, when it is reviewed, then it is deleted or left under `tests/` as a skipped integration test; no spike code is merged into `Enterprise.Gpt.Api` or `Enterprise.Gpt.Service`.

#### US-002: `[enabler]` Keep the licence out of git and materialize it in CI only

- **Story**: `[enabler]` Add a `.gitignore` entry for `Aspose.Total.NET.lic` beside the existing secrets block (`*.pfx`, `*.snk`, `*.p12`, `secrets.json`), and add a step to `api-ci.yml`'s jobs that writes the file from a repository secret before any build or test step that could construct an Aspose object. Unblocks US-003 and US-102.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a clean checkout, when the repository is searched, then no `.lic` file of any kind is tracked by git, and `.gitignore` names `Aspose.Total.NET.lic` in the same block as `*.pfx`/`*.snk`/`*.p12`/`secrets.json`.
  - Given `api-ci.yml` running on `push` to `master` or on a same-repo pull request, when the licence-materialization step runs, then the secret is written to the path `AsposeLicense` reads its embedded resource from before the build step that compiles it in, and the resulting binary's `IsLicensed` is `true` for all three products.
  - Given `api-ci.yml` running on a fork pull request, when the licence-materialization step runs, then it completes with no error and writes nothing — GitHub does not expose repository secrets to a fork PR's workflow run, and the step must not treat that absence as a failure.
  - Given the workflow file, when it is reviewed, then the secret's value is never echoed, logged, or interpolated into a shell command via `${{ }}`, matching the interpolation-avoidance pattern already used elsewhere in `api-ci.yml`.

#### US-003: `[enabler]` Aspose-dependent tests skip cleanly when unlicensed, and CI proves they still ran

- **Story**: `[enabler]` Add a shared xUnit trait (for example `[Trait("Category", "Aspose")]`) and a helper that checks the relevant product's `License.IsLicensed` at test start, skipping with a reason naming the missing licence rather than running unlicensed or asserting a false pass. Add a step to `api-ci.yml`'s `unit` job that asserts the trait's tests actually executed — not skipped — on `push` to `master` and on a same-repo pull request, following the reasoning in `docs/ci/pull-request-checks.md` §3 about a check that must not report green over untested code. Unblocks every Aspose-authored extractor, renderer, and parity test in EP-2 through EP-4.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-002
- **Status**: Not started.
- **Acceptance criteria**:
  - Given an Aspose-dependent test running with no licence applied, when it executes, then it reports skipped with a reason naming the missing licence, never a pass and never a failure that looks like a code defect.
  - Given the same test running with a successfully applied licence, when it executes, then it runs to completion and is counted as executed, not skipped.
  - Given `api-ci.yml`'s `unit` job on `push` to `master` or a same-repo pull request, when the run completes, then a step asserts at least one Aspose-trait test executed (not skipped) in that run, and the job fails if none did.
  - Given the identical job on a fork pull request, when the run completes, then the assertion step itself is skipped rather than failed, because a fork contributor has no way to satisfy a check gated on a secret they cannot receive.
  - Given a developer running `dotnet test --filter "Category!=Integration"` on a machine with no licence secret, when the suite completes, then every non-Aspose test still passes and no Aspose-trait test is reported as a failure.

#### US-004: `[enabler]` Measure the packaging and cold-start cost against the Windows target

- **Story**: `[enabler]` Publish the API with all three Aspose packages referenced (even before any code uses them), deploy the resulting artifact to a Windows App Service matching the target SKU, and record publish output size, deployed zip size, cold-start time, and `api-ci.yml`'s restore-time delta against the current baseline. Unblocks the decision, recorded as an exit gate rather than a story of its own, of whether any Aspose engine ships selected by default.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-001
- **Status**: Not started.
- **Acceptance criteria**:
  - Given the API built with the three Aspose packages referenced, when the publish output is measured, then its size delta against the pre-Aspose baseline is recorded in this story's own notes, in megabytes.
  - Given the same build packaged for `WEBSITE_RUN_FROM_PACKAGE = 1`, when the deployed zip is measured, then its size delta is recorded the same way.
  - Given the artifact deployed to a Windows App Service on the target SKU, when a cold start is measured (app stopped, then a first request timed to first byte), then the delta against the pre-Aspose baseline is recorded.
  - Given `api-ci.yml`'s `unit`/`integration` restore steps, when they run against the Aspose-referencing `Enterprise.Gpt.Service.csproj`, then the restore-time delta against the current CI baseline is recorded.
  - Given all four measurements, when they are reviewed, then a go/no-go note states whether any Aspose engine may ship selected in a committed `appsettings.json` at this cost, or whether it ships available-but-`Legacy`-by-default pending further work.

#### US-005: `[enabler]` Inventory the Windows App Service target's font faces

- **Story**: `[enabler]` Enumerate the font faces available on a Windows App Service instance matching the deployment target, and compare them against the faces the EP-4 print template will name. Unblocks US-401's font choices and US-405's substitution-telemetry story.
- **Priority**: P1 · **Estimate**: S · **Depends on**: —
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a Windows App Service instance on the target SKU, when its installed font faces are enumerated, then the list is recorded in this story's own notes.
  - Given the print template's chosen body, heading, and monospace faces, when they are checked against that list, then each is confirmed present, or the template's choice is revised to a face that is.
  - Given the same inventory, when it is compared against `Export/Fonts/README.md`'s documented OS-fallback order (Inter, Segoe UI, DejaVu Sans, Liberation Sans, Noto Sans, Arial), then any face on that list absent from the Windows target is named explicitly rather than assumed present.

### EP-1: The extraction engine seam

#### US-101: `[enabler]` Bind and validate the per-extension engine map

- **Story**: `[enabler]` Add `Enterprise.Gpt.Service/Settings/DocumentExtractionOptions.cs` (`SectionName = "Documents:Extraction"`, `Dictionary<string, DocumentEngines> Engines`) and a new `Enterprise.Gpt.Common/Enums/DocumentEngines.cs` (`Legacy = 1, Aspose = 2`, numbered from 1 like `SheetColumnType`/`JobStatus`). Register with `AddOptions<DocumentExtractionOptions>().Bind(...).Validate(...).ValidateOnStart()`, rejecting a key that does not parse to a `FileExtensions` member, a value outside `DocumentEngines`, and an extension named `Aspose` with no Aspose implementation (`.pptx`/`.xlsx`/`.csv` only), each message naming the full colon-delimited key. Unblocks US-102, US-103.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started.
- **Acceptance criteria**:
  - Given no `Documents:Extraction` section is configured, when the application starts, then every extension resolves to `Legacy` and startup succeeds.
  - Given `Documents:Extraction:Engines:.pptx = Aspose`, `.xlsx = Aspose`, `.csv = Aspose`, when the application starts, then validation accepts the map — pending the licence check in US-102.
  - Given a key that does not parse to a `FileExtensions` member (for example `.pptxx`), when the application starts, then it fails with `OptionsValidationException` naming `Documents:Extraction:Engines:.pptxx`.
  - Given a key naming an extension with no Aspose implementation (for example `.pdf` set to `Aspose`), when the application starts, then it fails naming that key and stating no Aspose extractor exists for it.
  - Given a value that is not a recognized `DocumentEngines` member, when the application starts, then it fails naming the offending key and its value.

#### US-102: `[enabler]` Apply the licence bootstrap at composition

- **Story**: `[enabler]` Add `Enterprise.Gpt.Service/Documents/Aspose/AsposeLicense.cs`, applying `Aspose.Words.License.SetLicense`, `Aspose.Cells.License.SetLicense`, and `Aspose.Slides.License.SetLicense` from one embedded `Aspose.Total.NET.lic` resource, invoked once in `Program.cs` before any `IDocumentTextExtractor`/`IConversationExportRenderer` registration runs — mirroring how `GlobalFontSettings.FontResolver` is set inside `ExportRendererRegistration` at composition rather than on first use. When `Documents:Extraction:Engines` or `Export:Engines` selects `Aspose` for a format whose product's `License.IsLicensed` is `false`, startup fails naming the config key. Unblocks US-103 and EP-4's US-403.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-001, US-002
- **Status**: Not started.
- **Acceptance criteria**:
  - Given the embedded resource is present and valid, when the application starts, then all three `SetLicense` calls succeed before `ExtractionEngineRegistration` or the export registration runs.
  - Given the embedded resource is absent (no CI secret, no local `.lic`), when the application starts and every engine map resolves to `Legacy`, then startup succeeds with no error — the absence of a licence is only a problem when something asks to use it.
  - Given the embedded resource is absent and `Documents:Extraction:Engines:.xlsx = Aspose`, when the application starts, then it fails with a message naming `Documents:Extraction:Engines:.xlsx` and stating that Aspose.Cells is not licensed.
  - Given the resource is present but malformed (corrupted or expired), when `SetLicense` throws, then the failure is caught, logged once per product with no licence content in the log, and treated identically to "no licence" for every subsequent validation.

#### US-103: `[enabler]` Register exactly one extractor per extension from the resolved map

- **Story**: `[enabler]` Add `Enterprise.Gpt.Api/Documents/ExtractionEngineRegistration.cs` (`internal static class`), a direct sibling of `ExportRendererRegistration`, that reads the validated `DocumentExtractionOptions` and registers exactly one `IDocumentTextExtractor` per `FileExtensions` member — the `Legacy` implementation unless the map names `Aspose` for that extension, in which case the Aspose implementation. Replaces the five unconditional `AddSingleton<IDocumentTextExtractor, …>()` calls at `Program.cs:789-793` with a call into this class. Unblocks EP-2's and EP-3's extractors, which this registration is what wires into DI at all.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101, US-102
- **Status**: Not started.
- **Acceptance criteria**:
  - Given the shipped default configuration (empty `Documents:Extraction`), when the application starts, then `DocumentTextExtractorFactory` resolves the identical five `Legacy` extractors it resolves today, with no behavior change for any extension.
  - Given `.xlsx` mapped to `Aspose`, when the application starts, then `DocumentTextExtractorFactory.Resolve(FileExtensions.Xlsx)` returns the Aspose implementation, and no `Legacy` `SpreadsheetTextExtractor` is registered at all.
  - Given the registration completes for any valid map, when `DocumentTextExtractorFactory` is constructed, then its own two-extractors-one-extension guard never fires — this registration is asserted, by test, to register exactly one candidate per extension in every case the validated map allows.
  - Given `.pdf`, `.doc`, `.docx`, `.md`, and `.txt`, when any engine map is applied, then each always resolves to its existing `Legacy` extractor, because no Aspose implementation for these extensions is registered by this epic — enforced by the same validation in US-101 that rejects an unsupported extension/engine pairing.

#### US-104: Name the winning engine in the startup log

- **Story**: As an operator, I want the startup log to name which engine won each extension, so I can confirm a configuration change took effect without inferring it from behavior.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-103
- **Status**: Not started.
- **Acceptance criteria**:
  - Given the application starts with any valid engine map, when `DocumentTextExtractorFactory`'s existing startup log line is emitted, then it additionally names the engine (`Legacy`/`Aspose`) that produced each listed extension.
  - Given `.xlsx` mapped to `Aspose` and every other extension left at its default, when the log is read, then only `.xlsx` is reported as `Aspose` and every other extension is reported as `Legacy`.
  - Given the log line, when it is inspected, then it carries no document content and no file name — only the fixed extension/engine pairing.

### EP-2: Aspose spreadsheet & CSV extraction, with the parity harness

#### US-201: `[enabler]` Aspose `.xlsx` extractor over `SheetAssembler`

- **Story**: `[enabler]` Add an Aspose `.xlsx` extractor implementing `ISheetStructureExtractor`, parsing with Aspose.Cells and feeding the resulting sheet/row/cell data into the existing `SheetAssembler`/`SheetSegmentBuilder` unchanged, so row windows, the schema card, and column-type inference are produced by the identical code the `Legacy` `SpreadsheetTextExtractor` already runs. `SourceNumber` is the workbook's own sheet order, read from Aspose.Cells' own worksheet collection. Unblocks US-203, US-204, US-206.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-103
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `.xlsx` mapped to `Aspose`, when a workbook is uploaded, then the extractor implements `ISheetStructureExtractor` — confirmed by a test asserting `DocumentService.ExtractAsync`'s `is ISheetStructureExtractor` check is `true` for it, since missing this interface would silently disable sheet persistence and `sheet_query` with no error anywhere.
  - Given a multi-sheet workbook, when it is extracted, then each sheet's `SourceNumber` is its 1-based ordinal position in Aspose.Cells' own worksheet collection, matching the meaning `SpreadsheetTextExtractor` already gives it from `WorkbookPart.Workbook.Sheets`.
  - Given the extracted rows and columns, when they are handed to `SheetAssembler`, then no code in the Aspose extractor duplicates row-window sizing, schema-card construction, or column-type inference — a code-search-backed test asserts the extractor calls `SheetAssembler`/`SheetSegmentBuilder` rather than reimplementing their logic.
  - Given a workbook with a sheet that has no rows, when it is extracted, then it produces zero segments for that sheet rather than throwing, matching `SpreadsheetTextExtractor`'s existing behavior.
  - Given the extractor is registered as a singleton, when it processes two workbooks concurrently under `BackgroundJobs:MaxConcurrent`, then no Aspose `Workbook` instance is held in an instance field, and the two extractions do not interfere with each other's results.

#### US-202: `[enabler]` Aspose `.csv` extractor reusing the existing sniffing pass

- **Story**: `[enabler]` Add an Aspose `.csv` extractor implementing `ISheetStructureExtractor`, reusing `CsvTextExtractor`'s own delimiter-sniffing pass (comma/semicolon/tab, ranked by per-line hit count) and its UTF-16 BOM detection ahead of handing the resolved delimiter to `Aspose.Cells.TxtLoadOptions.Separator` — which takes one explicit character with no auto-detection of its own — then feeds the parsed rows into `SheetAssembler`/`SheetSegmentBuilder` unchanged, treating the file as a single sheet exactly as `CsvTextExtractor` does. Unblocks US-204, US-206.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a semicolon- or tab-delimited file with a `.csv` extension, when it is extracted by the Aspose path, then its columns are recognized correctly rather than collapsed into one column — the sniffing pass runs before `TxtLoadOptions.Separator` is set, not after.
  - Given a `.csv` file with a UTF-16 byte-order mark, when it is extracted by the Aspose path, then it decodes correctly, matching `CsvTextExtractor`'s existing behavior; if Aspose.Cells' own BOM handling diverges, the extractor performs its own detection ahead of the load exactly as it performs its own delimiter sniffing.
  - Given the extracted rows, when they reach `SheetAssembler`, then the file is modeled as a single sheet (`SheetIndex = 1`, a derived sheet name), matching `CsvTextExtractor`'s existing contract.
  - Given the extractor is registered, when `GET api/documents/file-extensions` is called with `.csv` mapped to `Aspose`, then `.csv` is still reported among the supported extensions — no change to that endpoint's derivation from the factory.

#### US-203: `[enabler]` Bound Aspose.Cells' memory footprint under load

- **Story**: `[enabler]` Enforce every `Sheets:*` ceiling (`MaxRowsPerSheet`, `MaxRowsPerUpload`, `MaxColumnsPerSheet`, `MaxCharactersPerUpload`) while the Aspose `.xlsx` parse is in progress, using `LoadOptions.LoadFilter`/`MemorySetting` rather than after Aspose.Cells has materialized the whole workbook, and measure peak working set under `BackgroundJobs:MaxConcurrent` concurrency against several large workbooks. Unblocks confidence in US-201 shipping for real users; must land before any deployment sets `.xlsx` to `Aspose`.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-201
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a sheet with exactly `Sheets:MaxRowsPerSheet` rows, when it is uploaded through the Aspose path, then it succeeds; given `MaxRowsPerSheet + 1`, then it is refused with a message naming the limit, before the excess rows are fully materialized in memory.
  - Given a workbook whose total cell text would exceed `Sheets:MaxCharactersPerUpload`, when it is uploaded through the Aspose path, then it is refused with a message naming the limit — measured with the same construction the `Legacy` path's own decompression-bomb tests use (a workbook whose compressed size is small relative to its inflated content).
  - Given `Documents:MaxFileSizeBytes`-sized workbooks uploaded concurrently at `BackgroundJobs:MaxConcurrent`, when peak working set is measured, then it is recorded against the `Legacy` path's own measured peak for the identical scenario, and any regression beyond a documented, justified margin is called out rather than accepted silently.
  - Given `LoadOptions.LoadFilter`/`MemorySetting` are configured, when a workbook well under every ceiling is uploaded, then extraction still completes correctly — the memory bound must not silently truncate a legitimate sheet.

#### US-204: `[enabler]` The segment-parity harness for `.xlsx` and `.csv`

- **Story**: `[enabler]` Build a parity test harness that runs both the `Legacy` and `Aspose` extractors over the same `WorkbookBuilder`-synthesized `.xlsx` fixtures and the same synthesized `.csv` fixtures, and asserts segment-for-segment equality: text content, `SourceNumber`, schema-card content, row-window boundaries, and column-type inference. This is the headline deliverable of EP-2 — the concrete, automated answer to "can these two engines actually be compared." Unblocks US-206 and US-303 (the `.pptx` extension of this same harness).
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-201, US-202
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a multi-sheet `WorkbookBuilder` fixture spanning more than one chunk's worth of rows, when both engines extract it, then their produced segment lists are equal — same count, same text per segment, same `SourceNumber` per segment.
  - Given a `.csv` fixture with a non-comma delimiter and a UTF-16 BOM, when both engines extract it, then their produced segments are equal.
  - Given a fixture with a column whose sampled cells are mixed types, when both engines' output reaches `SheetAssembler`'s column-type inference, then both resolve to the identical `SheetColumnType`, since both paths call the same inference code — a divergence here would indicate the Aspose extractor is feeding `SheetAssembler` differently shaped input, not that inference itself disagrees.
  - Given a deliberately corrupt fixture, when both engines attempt extraction, then both fail as `ValidationException` (US-205), and the harness asserts this equivalence of *failure mode*, not merely of successful output.
  - Given the harness is run in CI, when licensing is unavailable (a fork PR), then it is skipped under the Aspose trait (EP-0, US-003) rather than run against unlicensed, watermark-contaminated Aspose output.

#### US-205: A malformed or password-protected spreadsheet fails cleanly

- **Story**: As a chat user, I want an unreadable spreadsheet to fail with a clear error regardless of which engine is configured, so that a bad upload never surfaces as an unhandled 500.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-201, US-202
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a truncated or corrupt `.xlsx` uploaded while `.xlsx` is mapped to `Aspose`, when extraction runs, then it fails as a `FluentValidation.ValidationException` naming the file as unreadable, matching the shape `PresentationTextExtractor`'s exception filter already produces for `.pptx`.
  - Given a password-protected `.xlsx` uploaded the same way, when extraction runs, then it fails the same way rather than hanging or throwing an unfiltered exception.
  - Given a `.csv` containing a genuinely unparseable structure under `.csv` mapped to `Aspose`, when extraction runs, then it fails as a `ValidationException` with the same shape as the `.xlsx` case, not an opaque 500.
  - Given the identical malformed fixtures run against the `Legacy` engine, when the two are compared, then both surface as a 400 `validation-error` problem to the caller — the wire contract is unchanged by which engine failed.

#### US-206: Establish the CSV stray-quote tolerance, or the documented fallback

- **Story**: As an operator deciding whether `.csv` may be flipped to `Aspose`, I want it proven that the Aspose path tolerates the same real-world CSV quirk `CsvHelper`'s cleared `BadDataFound` already tolerates, so that flipping the engine does not regress an upload that works today.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-202, US-204
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a `.csv` field containing a stray quote in an unquoted position (for example `5" pipe`, `Bob "Bo" Smith` — the exact shipped policy `docs/prd/sheet-ingestion/sheet-ingestion.md` US-103 records for `CsvTextExtractor`), when it is uploaded under `.csv` mapped to `Aspose`, then the upload succeeds and the cell's raw text is preserved verbatim, matching the `Legacy` engine's behavior — attempted first through `TxtLoadOptions.HasTextQualifier`/`TreatQuotePrefixAsValue`, since Aspose.Cells has no direct `BadDataFound`-style toggle.
  - Given that tolerance is not reachable through any combination of `TxtLoadOptions` settings, when this story concludes, then `.csv` is documented as staying on `Legacy` permanently in the shipped engine map, and startup validation (US-101) is extended to reject `.csv` mapped to `Aspose` outright — a designed outcome the per-format map exists to make cheap, not a failure of this PRD, since `.pptx`/`.xlsx` remain independently flippable.
  - Given whichever outcome this story reaches, when it is recorded, then the parity harness (US-204) is updated to either include the stray-quote fixture in its equality assertions or to document why `.csv` parity intentionally stops short of it.

### EP-3: Aspose presentation extraction

#### US-301: `[enabler]` Aspose `.pptx` extractor preserving slide order and speaker notes

- **Story**: `[enabler]` Add an Aspose `.pptx` extractor implementing `IDocumentTextExtractor`, parsing with Aspose.Slides, emitting one segment per slide with `SourceNumber` from the presentation's own slide order and including speaker notes text, matching `PresentationTextExtractor`'s existing contract exactly. Unblocks US-303.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a multi-slide presentation uploaded while `.pptx` is mapped to `Aspose`, when it is extracted, then each slide's `SourceNumber` is its 1-based ordinal position in the presentation's own slide order, matching `PresentationTextExtractor`'s numbering from `SlideIdList`.
  - Given a slide carrying speaker notes, when it is extracted, then the notes text is included in that slide's segment, matching `PresentationTextExtractor`'s existing behavior.
  - Given a presentation with a slide that has no text content, when it is extracted, then it produces zero segments for that slide rather than throwing.
  - Given the extractor is registered as a singleton, when two presentations are processed concurrently, then no Aspose `Presentation` instance is held in an instance field.

#### US-302: A malformed or password-protected `.pptx` fails cleanly

- **Story**: As a chat user, I want an unreadable presentation to fail with a clear error under the Aspose engine, so that a bad upload never surfaces as an unhandled 500.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-301
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a truncated or corrupt `.pptx` uploaded while `.pptx` is mapped to `Aspose`, when extraction runs, then it fails as a `ValidationException` naming the file as unreadable.
  - Given a password-protected `.pptx` uploaded the same way, when extraction runs, then it fails the same way rather than hanging.
  - Given the identical malformed fixture run against the `Legacy` engine, when the two are compared, then both surface as a 400 `validation-error` problem — the wire contract is unchanged by which engine failed.

#### US-303: Extend the parity harness to `.pptx`

- **Story**: As the platform, I want `.pptx` covered by the same segment-parity guarantee `.xlsx`/`.csv` already have, so a `.pptx` engine flip carries the same proven equivalence.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-204, US-301
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a multi-slide `PresentationBuilder` fixture with speaker notes on at least one slide, when both engines extract it, then their produced segment lists are equal — same count, same text per segment (notes included), same `SourceNumber` per segment.
  - Given a deliberately corrupt `PresentationBuilder` fixture, when both engines attempt extraction, then both fail as `ValidationException`, and the harness asserts this equivalence of failure mode.
  - Given the harness runs in CI, when licensing is unavailable, then the `.pptx` cases are skipped under the same Aspose trait as the `.xlsx`/`.csv` cases.

### EP-4: Template-driven export

#### US-401: `[enabler]` The print-oriented export template and its composer

- **Story**: `[enabler]` Add `Service/Files/conversation-export-print.html`, a new template authored in a Word-compatible CSS subset — tables, simple selectors, inline styles, a light ground, no custom properties, no flex, no grid — and `IExportHtmlComposer`, composing it from the same `ConversationExportDocument` model every existing renderer consumes. The existing `conversation-history.html` (a dark-themed browser artifact) is left untouched. Unblocks US-402, US-404.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a `ConversationExportDocument` with several messages, when `IExportHtmlComposer` composes it, then the resulting HTML uses only tables, simple element/class selectors, and inline styles — asserted by a test that fails on any `display: flex`, `display: grid`, or `var(--…)` occurrence in the template or the composed output.
  - Given a message whose stored `htmlContent` is null (persisted before server-side rendering existed), when it is composed, then the message's `Markdown` text is used instead, HTML-escaped, matching how the existing renderers already handle this case.
  - Given the composed HTML, when it is loaded by Aspose.Words with `HtmlLoadOptions`, then the resulting `Document` opens without a parse error and preserves message order.
  - Given the print template's chosen fonts, when they are checked against US-005's Windows App Service font inventory, then each is confirmed present or explicitly named as a substitution risk carried into US-405.

#### US-402: `[enabler]` Refuse every external resource during HTML import

- **Story**: `[enabler]` Set `HtmlLoadOptions.ResourceLoadingCallback` to refuse every external URI — any `http(s)://` reference the template or a message's `htmlContent` could carry — before the callback is ever exercised against real input. This is the PRD's one hard security requirement. Unblocks US-404.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-401
- **Status**: Not started.
- **Acceptance criteria**:
  - Given composed HTML containing an `<img src="https://attacker.example/…">` reference (as model output or an uploaded document could produce), when Aspose.Words loads it for export, then no outbound HTTP request is made for that URI, asserted by a test substituting a callback-tracking spy for the network layer.
  - Given the same HTML containing a `javascript:` link, when it is loaded, then the resulting document carries no live `javascript:` target, matching the existing renderers' link-scheme allowlist behavior (`http`, `https`, `mailto` only, applied to the rendered link's *destination*, not fetched).
  - Given `AsposeExportSecurityTests`, when it runs, then it is the pinned regression guard for this requirement — a change to the composer or the renderer that reintroduces a reachable remote fetch fails this test, not merely a manual review.

#### US-403: `[enabler]` `Export:Engines` map and licence-gated registration

- **Story**: `[enabler]` Add `Engines` (`Dictionary<string, DocumentEngines>`, keyed by wire token `docx`/`pdf`) to `Enterprise.Gpt.Service/Settings/ExportOptions.cs`, validated the same way as `Documents:Extraction:Engines` — an absent or empty map resolves to `Legacy`, an unknown token or engine value fails startup naming the key, and selecting `Aspose` with no successfully applied Aspose.Words licence fails startup. Extend `ExportRendererRegistration` to register exactly one renderer per format from the resolved map, composing with the existing `Export:DisabledFormats` check. Unblocks US-404.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-102
- **Status**: Not started.
- **Acceptance criteria**:
  - Given no `Export:Engines` configured, when the application starts, then `docx` and `pdf` resolve to their existing `Legacy` renderers (`WordExportRenderer`, `PdfExportRenderer`) with no behavior change.
  - Given `Export:Engines:docx = Aspose` with a successfully applied Aspose.Words licence, when the application starts, then the Aspose Word renderer is registered for `docx` and `WordExportRenderer` is not.
  - Given `Export:Engines:pdf = Aspose` with no successfully applied licence, when the application starts, then it fails naming `Export:Engines:pdf` and stating Aspose.Words is not licensed.
  - Given `Export:DisabledFormats: ["pdf"]` and `Export:Engines:pdf = Aspose` together, when the application starts, then `pdf` is not registered at all — `DisabledFormats` takes precedence over the engine map, matching the existing precedent that a disabled format's renderer is never registered regardless of what else is configured.
  - Given an `Export:Engines` key outside `{docx, pdf}` (for example `html`), when the application starts, then it fails naming the key, since no Aspose renderer exists for any other format.

#### US-404: Aspose Word and PDF export renderers

- **Story**: As a chat user, I want my conversation export to look the same whether it was produced by the existing renderer or the Aspose one, so switching engines is invisible to me beyond whatever fidelity difference prompted the switch.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-401, US-402, US-403
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `docx` mapped to `Aspose`, when a conversation is exported, then the Aspose renderer loads the composed print-template HTML into an Aspose.Words `Document` and saves it with `SaveFormat.Docx`, returning a `ConversationExport` with the same `ContentType`/`FileName` shape the `Legacy` renderer already returns.
  - Given `pdf` mapped to `Aspose`, when a conversation is exported, then the renderer saves the same loaded document with `SaveFormat.Pdf`, requiring no `Aspose.PDF` package reference.
  - Given a conversation with no messages, when exported through either Aspose renderer, then the response is a valid, openable document rather than an error — matching the `Legacy` renderers' existing empty-conversation behavior.
  - Given a message containing markdown syntax (headings, lists, code blocks), when it is exported through the Aspose `docx` path, then the rendered document preserves that structure — verified against the composed HTML's own structure, not byte-for-byte against the `Legacy` renderer's Open XML output, since the two engines are not required to produce identical bytes, only equivalent readable documents.
  - Given the Aspose PDF renderer, when it is exercised in CI, then it runs under the Aspose trait and skips cleanly when unlicensed, exactly as the extraction tests do.

#### US-405: Surface PDF font substitution instead of failing silently

- **Story**: As an operator, I want to know when the Aspose PDF export substituted a font, so a fidelity gap is visible in telemetry rather than discovered by a user complaint.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-404, US-005
- **Status**: Not started.
- **Acceptance criteria**:
  - Given a print-template face absent from the deployment (per US-005's inventory or a future drift from it), when a PDF is exported through Aspose, then an `IWarningCallback` captures the substitution and it is recorded as a metric or log entry naming the requested and substituted face — never silently accepted.
  - Given a PDF export where every requested face is available, when it completes, then no substitution warning is recorded.
  - Given the substitution telemetry, when it is inspected, then it carries no document content and no conversation identity — only the font names involved.

#### US-406: Markdown never acquires an Aspose renderer

- **Story**: As the platform, I want it structurally impossible for `ConversationExportFormats.Markdown` to be served by an Aspose renderer, so the deliberate fidelity decision in §2 cannot be silently reversed by a future configuration change.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-403
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `Export:Engines` accepts only the keys `docx`/`pdf` (US-403's own validation), when a value of `md` or any other non-`docx`/`pdf` key is configured, then startup fails naming the key — the same "unknown token" failure US-403 already specifies, restated here as the guard that keeps Markdown unreachable by this mechanism.
  - Given the registered renderer set after any valid engine map is applied, when it is inspected, then exactly one `IConversationExportRenderer` exists for `ConversationExportFormats.Markdown` — the existing `MarkdownExportRenderer` — with no code path that could register a second one.

### EP-5: Rollout, observability & rollback

#### US-501: Emit per-extraction and per-export engine metrics

- **Story**: As an operator, I want to see which engine handled each extraction and export, and how long it took, so a regression or a cost surprise after flipping a format shows up as a number rather than a support ticket.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-103, US-403
- **Status**: Not started.
- **Acceptance criteria**:
  - Given an extraction completes, when its metric is recorded, then it rides the existing `Enterprise.Gpt.Chat` meter (beside `ChatMetrics.RecordSheetIngestion`), tagged with the extension, the engine (`Legacy`/`Aspose`), the outcome (success/failure), and duration — no document content, file name, or document identity in any tag.
  - Given an export completes, when its metric is recorded, then it rides the same meter, tagged with the format, the engine, the outcome, and duration.
  - Given no Application Insights connection string is configured, when either metric is recorded, then it is still recorded to the meter and the distro is still skipped, matching the existing behavior documented in `docs/observability/request-logging.md` §7.1.

#### US-502: Extend `ExportAvailabilityLogger` to report the engine

- **Story**: As an operator, I want the startup log that already reports which export formats are available to also say which engine produced each one, so a configuration change is confirmed the same way US-104 confirms it for extraction.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-403
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `docx` mapped to `Aspose` and `pdf` left at `Legacy`, when `ExportAvailabilityLogger` runs at startup, then its log line names `docx` as `Aspose` and `pdf` as `Legacy`.
  - Given `pdf` mapped to `Aspose` with no successfully applied licence (a state US-403 already prevents from reaching startup successfully), when this story's tests construct the pre-failure state directly, then the logger's derivation logic still correctly attributes an unavailable format to its actual cause (no font vs. disabled vs. no licence) rather than collapsing all three into one message.

#### US-503: Rehearse the rollback

- **Story**: As an operator, I want a documented, exercised procedure for reverting either engine map to `Legacy`, so a regression discovered after enabling Aspose for a format is a configuration edit and a restart, not an incident.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-204, US-404
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `Documents:Extraction:Engines` or `Export:Engines` set to `Aspose` for one or more entries, when either is flipped back to empty/`Legacy` and the application restarts, then no redeploy beyond the restart is required and no migration runs, because none exists.
  - Given a document ingested while `.xlsx` was mapped to `Aspose`, when the engine map is later flipped to `Legacy` and the document is re-opened, then its already-persisted segments, chunks, and sheet rows are untouched — the rollback affects only future ingestion, never past data.
  - Given a corpus containing documents ingested under both engines (a mixed-engine corpus, the ordinary outcome of flipping a map mid-operation), when `document_search`/`sheet_query` run against it, then both engines' segments are retrieved identically, because both are the identical `DocumentSegmentDto`/`SheetStructureDto` shape regardless of origin — this story records that fact rather than treating it as a risk requiring re-ingestion.
  - Given the rollback procedure, when it is documented, then it states explicitly that a mixed-engine corpus is an accepted, permanent state, not a transient one requiring cleanup — a passed parity harness (EP-2/EP-3) is what makes this acceptable rather than merely convenient.

### EP-6: Optional extensions

#### US-601: `[enabler]` Aspose Word text extractor, shipped inert

- **Story**: `[enabler]` Add an Aspose Word (`.doc`/`.docx`) text extractor implementing `IDocumentTextExtractor`, extending `DocumentExtractionOptions`' validation to accept `.doc`/`.docx` mapped to `Aspose` as a now-valid (but not shipped-selected) combination. Registered by `ExtractionEngineRegistration` only when explicitly configured; the shipped `appsettings.json` leaves both at `Legacy`. Unblocks the platform revisiting the OCR-vs-Aspose decision for Word by configuration alone, without a code change or a new PRD.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `.docx` mapped to `Aspose` in a test configuration, when a Word document is uploaded, then it is extracted as text through Aspose.Words rather than Document Intelligence.
  - Given the same extractor, when it is fed a scanned, image-only `.docx` with no text layer, then it produces zero or near-zero segments — the extractor makes no attempt at OCR, and this is recorded as the expected, documented behavior rather than a defect, matching this PRD's non-goal in §2.
  - Given the shipped `appsettings.json`, when it is inspected, then `.doc`/`.docx` remain mapped to `Legacy` (Document Intelligence), unchanged from before this epic.

#### US-602: `[enabler]` Aspose PDF text extraction via Aspose.Words, shipped inert

- **Story**: `[enabler]` Add an Aspose PDF text extractor implementing `IDocumentTextExtractor`, using Aspose.Words' own PDF import (`LoadFormat.Pdf`) rather than the excluded `Aspose.PDF` package — Aspose.Words can read a PDF's text layer for a document that carries one, though with no OCR capability for a scanned page. Registered only when explicitly configured; shipped `appsettings.json` leaves `.pdf` at `Legacy`.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `.pdf` mapped to `Aspose` in a test configuration, when a PDF with a native text layer is uploaded, then its text is extracted through Aspose.Words' PDF import.
  - Given the same extractor, when it is fed a scanned PDF with no text layer, then it produces zero or near-zero segments, and this is documented as the expected limitation rather than a defect.
  - Given the shipped `appsettings.json`, when it is inspected, then `.pdf` remains mapped to `Legacy` (Document Intelligence), unchanged.

#### US-603: `[enabler]` `.xls`/`.xlsm` extraction via Aspose.Cells, shipped inert

- **Story**: `[enabler]` Add Aspose extractors for `.xls` (legacy binary Excel) and `.xlsm` (macro-enabled workbook) implementing `ISheetStructureExtractor` over the existing `SheetAssembler`/`SheetSegmentBuilder`, extending `DocumentExtractionOptions`' validation to accept both as valid Aspose targets. `.xlsm`'s macro content is never executed or inspected — only cell data is read. Registered only when explicitly configured; both extensions remain unreadable (`Legacy` has no implementation for either) unless an operator opts in.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started.
- **Acceptance criteria**:
  - Given `.xls` mapped to `Aspose` in a test configuration, when a legacy binary workbook is uploaded, then it is extracted through the same `SheetAssembler`/`SheetSegmentBuilder` path `.xlsx` uses, producing row windows and a schema card.
  - Given `.xlsm` mapped to `Aspose` in a test configuration, when a macro-enabled workbook is uploaded, then its cell data is extracted identically, and no macro content is executed, inspected, or surfaced in any segment.
  - Given the shipped `appsettings.json`, when it is inspected, then neither `.xls` nor `.xlsm` appears in `Documents:Extraction:Engines`, and `UploadedFileValidator`/`GET api/documents/file-extensions` continue to reject both by default, unchanged from before this epic.

#### US-604: A guard test that EP-6 ships inert

- **Story**: As an operator, I want a test that fails the build if EP-6's extractors are ever accidentally enabled by default, so "shipped but off" stays true as the codebase evolves.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-601, US-602, US-603
- **Status**: Not started.
- **Acceptance criteria**:
  - Given the committed `appsettings.json`, when it is parsed and bound to `DocumentExtractionOptions`, then `.doc`, `.docx`, `.pdf`, `.xls`, and `.xlsm` are all absent from `Documents:Extraction:Engines` (equivalently, resolve to `Legacy`/unsupported).
  - Given the same file, when `.xls`/`.xlsm` are checked against `IDocumentTextExtractorFactory.SupportedExtensionNames` at the shipped configuration, then neither appears — both remain rejected uploads exactly as before this epic, confirming EP-6 changed no default-path behavior.

## 7. Milestones & rollout

**Phases**, derived from the epic dependency graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **Phase 1 — prove it's safe to build on** | EP-0 in full (US-001–US-005) | ~1 week |
| **Phase 2 — the engine seam** | EP-1 in full (US-101–US-104), unblocked by EP-0's licence and secret-handling stories alone | ~3–4 days |
| **Phase 3 — extraction parity** | EP-2 in full (US-201–US-206) and EP-3 in full (US-301–US-303), run concurrently — spreadsheet/CSV and presentation extraction share no file | ~2 weeks |
| **Phase 4 — export parity** | EP-4 in full (US-401–US-406). Depends only on EP-0's licence bootstrap, so in practice its early stories (US-401, US-402) can start alongside Phase 2 rather than waiting for Phase 3 to close | ~1.5 weeks |
| **Phase 5 — rollout, observability & rollback** | EP-5 in full (US-501–US-503) | ~0.5 week |
| **Phase 6 — optional extensions** | EP-6 in full (US-601–US-604) | ~1 week |

The MVP — the point at which the platform can compare a real Aspose engine against the incumbent on real documents for at least one format — is Phase 3 or Phase 4 closing, whichever a team prioritizes first; both depend on nothing but Phases 1 and 2. Phase 6 is explicitly last and explicitly optional: nothing in Phases 1 through 5 depends on it, and it exists to keep the platform's original "Word and PowerPoint with Aspose" instinct reachable by configuration rather than by a second PRD.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| GitHub does not expose repository secrets to a fork pull request's workflow run, so an Aspose-dependent test could run unlicensed and report a false pass | EP-0's US-003 makes every Aspose-dependent test self-skip, naming the missing licence, when unlicensed, and adds a CI assertion that the trait's tests actually ran (not skipped) on `push` to `master` and on same-repo pull requests — the same reasoning `docs/ci/pull-request-checks.md` §3 already applies to the `changes` job |
| Aspose.Cells materializes a whole workbook where the existing extractor streams, risking a memory blowup under concurrent large uploads | EP-2's US-203 enforces every `Sheets:*` ceiling via `LoadOptions.LoadFilter`/`MemorySetting` while parsing, and measures peak working set under realistic concurrency before `.xlsx` may ship selected |
| `CsvHelper`'s cleared `BadDataFound` tolerance for a stray quote in an unquoted field has no documented one-line equivalent in `Aspose.Cells.TxtLoadOptions` | EP-2's US-206 either proves the tolerance reachable through `HasTextQualifier`/`TreatQuotePrefixAsValue`, or documents `.csv` staying on `Legacy` permanently — a supported, designed outcome the per-format map exists to make cheap |
| ~227 MB of new native-backed packages regresses publish size, deployed zip size, cold start, or CI restore time on the Windows App Service target | EP-0's US-004 measures all four deltas before any Aspose engine ships selected in a committed `appsettings.json`, and records a go/no-go note rather than assuming the cost is acceptable |
| Reusing Aspose.Words' HTML importer for export reintroduces the SSRF the existing renderers deliberately avoid by dropping images | EP-4's US-402 sets `HtmlLoadOptions.ResourceLoadingCallback` to refuse every external URI before the callback is ever exercised against real input, pinned by a build-breaking regression test (`AsposeExportSecurityTests`), not a one-time review |
| The Windows App Service target's available fonts are unknown at drafting time, and CI (ubuntu) resolves fonts differently than the serving host | EP-0's US-005 inventories the target's faces before EP-4's template is finalized, and EP-4's US-405 surfaces any substitution Aspose.Words performs anyway through telemetry rather than letting it degrade silently |
| The Aspose licence file can expire or be revoked, and nothing today alerts an operator before a deployment starts failing to select an engine it could select yesterday | Recorded as an open question (§8) rather than solved here — the fail-loud startup behavior (FR-9) means an expired licence is caught immediately on the next restart, but no proactive alerting is designed in this PRD |
| `api-cd.yml` does not exist yet, so this PRD's CI-only licence handling has no deploy-time counterpart for the production App Service | Recorded as a non-goal (§2) and an open question (§8): the deploy-time equivalent is a stated dependency on `docs/prd/azure-infrastructure/azure-infrastructure.md`'s CD epic, not designed here |

**Rollout & rollback.** Both engine maps ship absent from the committed `appsettings.json`, which resolves to `Legacy` for every extraction extension and every export format — this PRD changes no deployment's behavior until an operator opts a format in. Enabling a format is a configuration edit and a restart; so is reverting one. No schema changes, no migrations, and no data changes accompany any part of this PRD — every Aspose extractor and renderer produces the identical DTO shapes the existing pipeline already persists and serves. A document ingested under one engine keeps the segments that engine produced even after the map is later changed; EP-5's US-503 records this explicitly as an accepted, permanent property of a mixed-engine corpus, made acceptable by the parity harness (EP-2/EP-3) having already proven the two engines produce equivalent segments for the formats it covers, not merely by convenience. The rehearsed rollback (EP-5, US-503) is: flip the map entry back to `Legacy` (or remove it), restart, and confirm previously ingested or exported content is unaffected.

## 8. Assumptions & open questions

**Assumptions.** Each is a guess a reviewer can veto.

- **The Aspose package facts in §4 of the invocation (versions, sizes, SkiaSharp as a transitive dependency, the HTML importer's CSS-support gaps) are taken as researched and locked by the product owner on 2026-08-30, not independently re-verified against Aspose's own release notes in this drafting session.** EP-0's US-001/US-004 are where the load-bearing subset of these claims — the licence mechanism and the packaging cost — gets proven empirically rather than left assumed.
- **`DocumentEngines { Legacy = 1, Aspose = 2 }` is a new, shared enum** rather than a per-surface string, binding directly from a configuration value through `Dictionary<string, DocumentEngines>`; a reviewer preferring plain strings would change EP-1's US-101 and EP-4's US-403, not the rest of this PRD.
- **`Export:Engines` binds directly onto the existing `ExportOptions` class** rather than a new nested options type, on the precedent that `DisabledFormats` and `Pdf` already live there; a reviewer preferring a separate `ExportEngineOptions` class would change only US-403's implementation shape.
- **No new permission id or admin-facing surface is introduced.** Engine selection is treated as deployment configuration an operator owns, not a product capability a user or administrator opts into — unlike `Summarization:Enabled`/`FileAgent:Enabled`, which gate a cost-bearing capability from being offered to users at all. A reviewer who wants engine choice exposed as an administrator setting would need a new epic, not a change to this one.
- **Failing hard at startup, rather than warning and falling back to `Legacy`, is the correct posture for every engine-map misconfiguration.** This matches every other `ValidateOnStart()` options class in the codebase and is treated as non-negotiable rather than a design choice worth relitigating per format.
- **Aspose.Words' PDF import (`LoadFormat.Pdf`, EP-6's US-602) can read a native PDF's text layer without the excluded `Aspose.PDF` package.** This is asserted from the invocation's researched facts and is deliberately scoped to EP-6 — an optional, defaulted-off epic — precisely because it is the least-verified claim in this document.
- **The CSV stray-quote tolerance (EP-2's US-206) is a best-effort parity target, not a blocking one.** Given the researched fact that `Aspose.Cells.TxtLoadOptions` has no documented one-line equivalent to `CsvHelper`'s `BadDataFound`, this PRD treats "`.csv` stays on `Legacy` permanently" as an equally acceptable outcome to "parity achieved," which is why US-206 is P1 rather than P0.
- **The deploy-time (CD) equivalent of EP-0's CI licence-materialization step is out of scope**, because `api-cd.yml` does not exist. This PRD assumes that work lands as part of `docs/prd/azure-infrastructure/azure-infrastructure.md`'s CD epic and is not designed here.

**Open questions.**

- **Does the Windows App Service image carry the font faces the print template names?** EP-0's US-005 answers this empirically before EP-4's template is finalized — *backend engineer, during EP-0*.
- **Is the roughly 227 MB package-weight cost acceptable against a cold-start SLO?** No cold-start target was supplied to this PRD; EP-0's US-004 measures the delta but this document does not set a pass/fail threshold against it — *product owner/operator, once EP-0's measurements land*.
- **Is `.csv` parity with `CsvHelper`'s `BadDataFound` tolerance actually reachable in Aspose.Cells?** EP-2's US-206 resolves this either way, but the answer is not yet known — *backend engineer, during EP-2*.
- **How should a corpus ingested under mixed engines be treated going forward — accept the drift, or offer a re-ingestion path?** EP-5's US-503 provisionally answers "accept," on the strength of the parity harness proving the two engines produce equivalent segments; a product owner with a use case sensitive to byte-exact provenance might want this revisited — *product owner, post-launch*.
- **What is the deploy-time mechanism for delivering the licence to the production Windows App Service, once `api-cd.yml` exists?** This PRD's licence handling covers CI only — *whoever authors the azure-infrastructure PRD's CD epic*.
- **Who owns rotating or renewing the Aspose licence, and how is an operator alerted if `License.IsLicensed` starts failing in a running deployment after an expiration?** The fail-loud startup behavior (FR-9) catches this on the next restart, but nothing in this PRD proactively alerts before that — *operator, before any Aspose engine ships selected in production*.
