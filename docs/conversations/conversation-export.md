# Conversation Export

How the API renders a stored conversation into a file a user can take out of the platform: a renderer registered per format rather than a branch, a font dependency that only one of the five formats has, a shared markdown walk and a shared theme that keep Word, PDF and HTML from disagreeing with each other, and a deliberate refusal to embed an image.

Audience: a backend engineer adding a sixth export format, operating a deployment that needs to know whether PDF export will actually work, or reviewing why this went to a document library instead of a headless browser. Read [Transcript Storage §11–§12](transcript-storage.md#11-server-rendered-html) first for `htmlContent`, the persisted rendering only `json` still carries — as one field of the stored document it serializes wholesale, not a template rendered from it — and for the route contract this document only elaborates on. The client side of this feature — the download menu, the store, and why the transport is a response body rather than a signed URL on that side too — is [Conversation Download](../ui/conversation-download.md).

Implements US-1501 of [the rebuild PRD](../prd/enterprise-ui-rebuild.md#us-1501-enabler-expose-conversation-export-over-http-b11).

## 1. Overview

The route already served `html` and `json` by reading the transcript's own `htmlContent` and stored documents (see [Transcript Storage §12](transcript-storage.md#12-export)). US-1501 added three more — `md`, `docx`, `pdf` — and restructured the service around them:

| Format | Reads or re-renders | Needs a font | Registered by default |
| --- | --- | --- | --- |
| `html` | Re-renders through the block model | No | Only if the template file is present |
| `json` | Reads the stored documents | No | Always |
| `md` | Reads the message's own markdown | No | Always |
| `docx` | Re-renders through the block model | No — the reader's own theme supplies glyphs | Always |
| `pdf` | Re-renders through the block model | **Yes** — glyphs are embedded | Yes — the five faces committed under `Export/Fonts/` are found by default |

Six decisions shape everything below, and each looks removable until you know what it prevents:

1. **The response is bytes, not a signed URL.** The PRD permitted either. Rendering to blob storage would have made every export — markdown included — depend on storage being configured, and would have left rendered transcripts sitting there for something to clean up. A render is comparatively cheap and happens once, on request, so the API just writes it into the response (§7).
2. **PDFsharp/MigraDoc 6.2.4, not QuestPDF.** Both are the realistic .NET options for laying out a PDF without a browser in the loop. PDFsharp/MigraDoc is MIT with no revenue threshold. QuestPDF's Community tier is source-available rather than MIT and is gated on the *company's* revenue, not the project's — the same reasoning that already keeps FluentAssertions v8 out of this repository's test projects. See `Enterprise.Gpt.Service.csproj`'s comment on the package reference.
3. **`export-renderer-not-configured` is a real state, not defensive code.** PDFsharp's cross-platform build resolves no font on its own and throws on the first glyph without a resolver, so whether PDF is offered at all is decided once, at startup, by whether a usable face was found (§3, §4). `Export:DisabledFormats` can additionally withdraw *any* format, and the HTML renderer is not registered when its template file is missing. All three reasons produce the same client-visible outcome — a typed 503 naming the format — and none of them is a bug.
4. **Five fonts are committed to the repository, so PDF export needs no operator action.** `Export/Fonts/README.md` remains the operator contract for a deployment that wants to re-brand — five role-named files, only the regular sans face required, an all-or-nothing fallback to the platform's own font directories if the configured directory supplies none — but the default is no longer "bring your own." Inter and JetBrains Mono are both under the SIL Open Font License, which permits redistribution, so a bare `mcr.microsoft.com/dotnet/aspnet` container renders a PDF exactly as any other deployment does, with no font package installed (§4, §6.2).
5. **`docx`, `pdf` and `html` all drop images and keep the alt text.** Embedding an image means the API fetching a URL that *model output chose*, from the API's own network position — a server-side request forgery surface with no legitimate purpose in a transcript export. `html` used to be the exception, because it read the transcript's own `htmlContent` and left the fetch to the reader's browser; rendering it from the same block model as the other two ended that exception (§5.2).
6. **One AST walk, three emitters.** `MarkdownBlockMapper` turns a message's markdown into one renderer-neutral block model that the Word, PDF and HTML renderers all consume, so the three formats cannot disagree about what a message meant — and the walk runs through the same hardened `MarkdownPipelines` that produces `htmlContent`, so the link-scheme allowlist protecting the stored transcript protects an export too (§5).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| Routing, ownership, format resolution | [`Service/Export/ConversationExportService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportService.cs) |
| The five format names and their `?format=` tokens | [`Service/Export/ConversationExportFormatNames.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportFormatNames.cs) |
| The renderer contract, and the reduced view a renderer sees | [`Service/Export/IConversationExportRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/IConversationExportRenderer.cs), [`ConversationExportDocument.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportDocument.cs) |
| The five renderers | [`Service/Export/Renderers/`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/) |
| The renderer-neutral block model, the enclosure flags renderers thread through it, and the Markdig walk that builds it | [`Service/Export/ExportBlocks.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportBlocks.cs), [`ExportBlockStyle.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportBlockStyle.cs), [`MarkdownBlockMapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/MarkdownBlockMapper.cs) |
| The one palette, type scale and font stack every rendered format reads | [`Service/Export/ExportTheme.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportTheme.cs) |
| The shared, hardened markdown pipeline (also used for `htmlContent`) | [`Service/Rendering/MarkdownPipelines.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownPipelines.cs) |
| The PDF font resolver and the roles it looks for | [`Service/Export/Fonts/ExportFontResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Fonts/ExportFontResolver.cs), [`Export/Fonts/README.md`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Fonts/README.md) |
| `Export` configuration | [`Service/Settings/ExportOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/ExportOptions.cs) |
| Which formats a deployment can actually serve, and startup registration | [`Api/Export/ExportRendererRegistration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Export/ExportRendererRegistration.cs), [`ExportAvailabilityLogger.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Export/ExportAvailabilityLogger.cs) |
| The 503 exception and its problem type | [`Service/Exceptions/ExportRendererNotConfiguredException.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ExportRendererNotConfiguredException.cs), [`Api/Problems/ProblemTypes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemTypes.cs) |
| The route itself | [`Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs) — `ExportConversationAsync` |

## 2. Quick start

```http
GET /api/conversations/9c1f1c8e-6a1e-4c1a-9f7a-2b3c4d5e6f70/export?format=docx
Authorization: Bearer <token>
```

```http
HTTP/1.1 200 OK
Content-Type: application/vnd.openxmlformats-officedocument.wordprocessingml.document
Content-Disposition: attachment; filename="Quarterly-report-wording.docx"; filename*=UTF-8''Quarterly-report-wording.docx
Cache-Control: no-store

<binary .docx package>
```

A format this deployment does not offer:

```http
GET /api/conversations/{id}/export?format=pdf

HTTP/1.1 503 Service Unavailable
Content-Type: application/problem+json

{
  "type": "/problems/export-renderer-not-configured",
  "title": "Export format not available",
  "status": 503,
  "detail": "Conversation export to pdf is not available in this environment.",
  "format": "pdf",
  "traceId": "…"
}
```

## 3. The renderer registry

[`ExportRendererRegistration.AddExportRenderers`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Export/ExportRendererRegistration.cs) runs once, at composition, and decides which of the five `IConversationExportRenderer` implementations get registered:

- **`json`, `md` and `docx`** register unless `Export:DisabledFormats` names their wire token. Nothing about them can fail to build.
- **`html`** additionally needs `Files/conversation-history.html` to exist on disk — checked at composition rather than left to a static initializer, because the alternative surfaces as a `TypeInitializationException` on the *first* export of any format, not just `html`, and no exception handler arm maps that to anything but a bare 500.
- **`pdf`** additionally needs [`ExportFontResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Fonts/ExportFontResolver.cs) to find a usable face. The resolver is built here, at startup, and assigned to `PdfSharp.Fonts.GlobalFontSettings.FontResolver` — a process-global PDFsharp documents as set-once, before any font operation — which is what makes "can this deployment render a PDF?" a startup answer instead of a per-request surprise.

`ConversationExportService` never sees any of that reasoning. It resolves `IEnumerable<IConversationExportRenderer>` into a lookup keyed by `ConversationExportFormats` and, when a caller asks for a format with no entry, raises `ExportRendererNotConfiguredException` (§4) rather than falling through to a 500. Registration overwrites by indexer rather than `ToDictionary`, deliberately: `ToDictionary` throws on a duplicate key, and that throw would land in the scoped service's constructor on every single export request — a 400 with a LINQ exception message in the body, because `ArgumentException` is the handler's bad-request arm.

[`ExportAvailabilityLogger`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Export/ExportAvailabilityLogger.cs), a hosted service, reports the outcome once at startup — through the real logging pipeline, not a throwaway `LoggerFactory` that composition would otherwise need and have nothing to dispose. It re-derives *why* a format is missing rather than being told: absent and named in `Export:DisabledFormats` means withdrawn on purpose; absent and unnamed means it could not be built, which today only ever means PDF with no usable font. That second case logs at `Warning`, because every entry in it is a route that will answer 503 to a request nobody configured it to refuse.

```jsonc
// appsettings.json
"Export": {
  "DisabledFormats": [],       // e.g. [ "pdf" ] to withdraw PDF deliberately
  "Pdf": {
    "FontDirectory": null      // defaults to "Fonts" beside the built assembly
  }
}
```

`Export:DisabledFormats` is validated at startup against the same token list the route accepts (`ConversationExportFormatNames.Supported`), so a typo fails app start rather than silently withdrawing nothing — the same bargain `PermissionEndpointFilter.Require` makes with a bad permission id.

## 4. The 503, and why it is not a 500 or a 400

[`ExportRendererNotConfiguredException`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ExportRendererNotConfiguredException.cs) derives from `Exception`, not `InvalidOperationException`: the global handler maps the latter to 400, which would report a *deployment* gap as a *client* mistake and send the caller into a retry loop over a state no retry can change. Mapped instead to 503 under `/problems/export-renderer-not-configured`, carrying a `format` extension — the request is well formed and the conversation exists, so this is an operator condition, not a bad request. That extension is what lets a client offering four formats disable the one that is unavailable rather than the whole download control; see [Conversation Download §5](../ui/conversation-download.md#5-error-handling) for how the client reads it.

This is one of the reasons the format resolution in `ConversationExportService.ResolveRenderer` is two steps rather than one: `ConversationExportFormatNames.TryParse` alone decides 400 (the token names nothing this API has ever supported); the registry lookup after it decides 503 (the token names something this API supports and this deployment does not offer today). Collapsing them would make "which formats does the API accept" and "which formats does this deployment offer" the same question, and they are not — the first is a contract, the second is an environment.

## 5. Reading vs. re-rendering, and the block model

`json` and `md` still **read** what persist time already computed or preserved — see [Transcript Storage §12.1](transcript-storage.md#121-three-formats-read-two-re-render) for what each of the two reads and why re-parsing `md`'s own markdown would be a worse export than the text itself.

`docx` and `pdf` never had the option: neither format has anywhere to put HTML markup or raw markdown text, so both need *structure*. `html` has the option and gives it up anyway (§5.2) — reading `htmlContent` would leave it the one format built from a different rendering of the same message than the other three, and an export that can disagree with itself is worse than the marginal cost of one more format walking the same block model. All three **re-render**, through one shared path:

```
message.Markdown → MarkdownPipelines.Parse (Markdig AST) → MarkdownBlockMapper.Map → IReadOnlyList<ExportBlock>
                                                                                        ↓               ↓                ↓
                                                                              WordExportRenderer  PdfExportRenderer  HtmlBlockWriter
```

### 5.1 The block model

[`ExportBlock`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportBlocks.cs) is deliberately smaller than markdown: `HeadingBlock`, `ParagraphBlock`, `CodeBlock`, `QuoteBlock`, `ListBlock`, `TableBlock`, `ThematicBreakBlock`, and — inline — `ExportRun`, a span of text carrying bold/italic/code flags and an optional link URL. There is no image block at all (§5.2). [`ExportBlockStyle`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportBlockStyle.cs) rides alongside it, a `[Flags]` enum (`Prompt`, `Quote`) each renderer threads through its own walk rather than a property on the block itself — what encloses a block is something only the renderer walking the tree knows, not something the block's meaning depends on. Building the model once and handing the *same* one to all three renderers is what guarantees a Word export, a PDF export and an HTML export of the same conversation cannot disagree about what a message's markdown meant; without it, "the renderers happened to interpret an edge case differently" would be a class of bug this design instead makes structurally impossible.

`MarkdownBlockMapper.Map` never throws: a message that fails to parse degrades to a single paragraph holding its own text rather than losing the message from the export. Two depth limits — `MaxDepth = 16` before a container is flattened into plain text, `MaxFlattenDepth = 32` before even that stops recursing — exist because flattening is itself recursive, and Markdig's own refusal to parse past 128 levels of nesting is the backstop the mapper's `catch` turns into a plain paragraph. That backstop matters specifically because a `StackOverflowException` is not catchable, and none of the recursion here is over trusted input.

### 5.2 Images are dropped, alt text is kept

`MarkdownBlockMapper` reads an image's alt text as ordinary runs and reads nothing else about it — none of the three rendered formats ever sees the URL. Embedding the image itself would mean the API making an outbound HTTP request to a URL that *model output chose*, from inside the API's own network position — a server-side request forgery surface with no legitimate purpose here, since a transcript export exists to preserve what was said, not to fetch what was linked.

`html` used to be the one exception: reading the transcript's own `htmlContent` left the `<img>` tag in place, and the fetch fell to the *reader's* browser, from the reader's own network position — the trust boundary that already applies to every page they open, and a genuinely different one from the API making the request itself. That exemption ended when `html` moved onto the shared block model. `docx`, `pdf` and `html` now all read the identical `IReadOnlyList<ExportBlock>`, which carries no image block at all, so an exported transcript fetches nothing when it is opened, in any of the three re-rendered formats.

### 5.3 The shared pipeline is what makes the export trust boundary the same one

[`MarkdownPipelines.Parse`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownPipelines.cs) is the one Markdig configuration this solution parses message text with — see [Transcript Storage §11](transcript-storage.md#11-server-rendered-html) for why the input is attacker-influenceable at all. `MarkdownBlockMapper` calls the same `Parse`, not a second pipeline, so the link-scheme allowlist (`http`, `https`, `mailto`; everything else blanked, including `javascript:`) and the `DisableHtml` raw-HTML refusal that protect the stored transcript protect an export identically. A `javascript:` URL is a live click target inside Word and inside most PDF readers exactly as it is inside a browser — the export renderers are the case that proves a second, independently-maintained pipeline would have been a second trust boundary to get right twice.

## 6. The Word, PDF and HTML renderers

### 6.1 `ExportTheme`: the one palette, type scale and font stack every renderer shares

[`ExportTheme`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportTheme.cs) is the single source every rendered format's appearance comes from — colours, faces and sizes for Word, PDF and HTML alike, so a conversation exported three ways no longer looks like three unrelated documents. Before it, each renderer transcribed its own handful of hex constants — the HTML template's palette (`#122842`, `#1c3c62`, `#4a9eff`, …) was invented and matched nothing in the design system, and Word and PDF each duplicated five hex constants of their own that agreed with neither the template nor each other.

It holds:

- **`Colors`** — nine values, transcribed **verbatim** from the application's own light theme in `enterprise-gpt-ui/src/styles/_tokens.scss` and named the same way (`--surface-2`, `--brand`, `--think-bg`, …), so a reviewer can check one against the other by eye. Three changed from what the old, invented palette used, and now match the app exactly rather than approximating it: the border colour (`D0D7DE` → `DCE5EE`), the code-block fill (`F3F5F7` → `EFF4F9`) and the link colour (`0B5FA5` → `14324F`).
- **`Fonts`** — an ordered sans and monospace stack (`Inter, Calibri, Segoe UI, system-ui, sans-serif` and `JetBrains Mono, Consolas, ui-monospace, monospace`) for HTML, plus a separate `WordSans`/`WordMono` pair for Word (see below).
- **`Sizes`** — type sizes in points, with one heading ramp replacing the two divergent ramps Word and PDF used to compute independently.
- **`CssVariables`** — the `:root` block [`HtmlExportRenderer`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/HtmlExportRenderer.cs) writes into the template's `{{THEME}}` placeholder (§6.3). This is what keeps `conversation-history.html` honest: the template carries **no literal colour, face or size of its own**, so every value a reader sees in an HTML export traces back to this one class — and [`ExportThemeTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/ExportThemeTests.cs) fails the build the day the template states a colour of its own or references a variable this class does not supply.

Exports are deliberately **single-theme** — there is no dark-mode PDF or `.docx`, because a printed page has no `prefers-color-scheme` to read, and a reader who reopens an export two years from now should see the same colours regardless of which theme the app happens to ship that day.

Two divergences from the app's own theme are deliberate, not oversights:

- **Code blocks render on a light surface (`--surface-2`) in all four formats**, a departure from the app's dark code chrome. Word and PDF are printed, and a dark block is a page of toner; making HTML match them rather than making HTML the one exception keeps the four formats consistent with each other, which is the property this theme exists to protect.
- **Word names `Calibri`, not the head of the theme's own sans stack.** A `.docx` carries no glyphs at all, and Word's own substitution for a family the reader's machine lacks is not deterministic — naming `Inter` and trusting Word to fall back sensibly would render differently depending on what happens to be installed wherever the document is opened. Naming what ships with Office is the one thing this renderer can make deterministic; the code face, `Consolas`, follows the same reasoning.

### 6.2 The Word and PDF renderers

**[`WordExportRenderer`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/WordExportRenderer.cs)** writes Office Open XML directly with the `DocumentFormat.OpenXml` SDK. Colours and sizes are named from `ExportTheme` on every paragraph style (§6.1); the one thing that is not is the face itself, for the reason §6.1 gives. Word, alone among the five renderers, still has no font-*availability* precondition — nothing about building the document depends on this deployment actually having a face, since Word substitutes for whatever it names at open time regardless of what the server had. It is built from the block model rather than from `htmlContent` because Word has no HTML importer to hand markup to short of an `altChunk`, which makes the document's contents depend on whichever version of Word opens it and on that Word being willing to run an import at all. Several structural choices in it exist only because Open XML's schema orders child elements strictly — `CT_PPrBase`, `CT_RPr`, `CT_TblBorders`, and `w:numbering`'s abstract-then-instance ordering — and a package assembled in a different order round-trips through this SDK's own reader without complaint while Word itself refuses to open it; that class of bug is what `ConversationExportServiceTests` runs `OpenXmlValidator` against (§9).

**[`PdfExportRenderer`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/PdfExportRenderer.cs)** consumes the identical block model and the identical theme, and lays it out with MigraDoc/PDFsharp, embedding glyphs as it goes — which is the one thing that makes PDF, alone among the five formats, dependent on a font actually being found (§3, §4). Bold is never *simulated*: PDFsharp documents its bold-simulation flag as unimplemented, so a deployment with no bold face renders bold text upright and unemboldened rather than synthetically thickened — a face that was never designed to be simulated bold is worse than no distinction at all. Faces resolve **all-or-nothing** across the two sources `ExportFontResolver` checks (the configured directory, then the operating system's own font directories) rather than merged per role, because filling a missing bold face from the platform while the body face came from the configured directory would set one document in two unrelated typefaces — an operator who supplied a face expects the whole document to be in it.

### 6.3 The HTML renderer

**[`HtmlExportRenderer`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/HtmlExportRenderer.cs)**, together with [`HtmlBlockWriter`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/HtmlBlockWriter.cs), is the renderer the design in §6.1 is built around: its template resolves entirely against `ExportTheme`'s `CssVariables`, and its content comes from the same `MarkdownBlockMapper` walk Word and PDF consume (§5.1) rather than from the stored `htmlContent`. `HtmlBlockWriter` encodes every span of text it writes — the blocks carry model output, and the pipeline that built them refuses raw HTML precisely so nothing downstream puts markup back in.

Substituting `{{THEME}}`, `{{TITLE}}`, `{{DATE}}` and `{{MESSAGES}}` into the template is a **single regex pass**, not four chained `Replace` calls. The two are not equivalent: HTML-encoding a conversation's own name leaves the literal text `{{MESSAGES}}` untouched, since none of its characters need encoding, so a conversation *named* `{{MESSAGES}}` had the whole rendered transcript spliced into its own `<title>` and `<h1>` by the later `Replace` in the chain — a real defect a code review caught rather than a hypothetical one, now covered by a regression test (§9).

## 7. The HTTP surface

`ExportConversationAsync` writes `export.Content` directly through `Results.File`, never through `TypedResults`: the payload is a rendered document, not a DTO this API otherwise serializes, and two of the five formats are not text at all. `Results.File` writes `Content-Disposition: attachment` with both a plain `filename` and an RFC 5987 `filename*`, so a conversation named using any script downloads under its own name — the CORS policy already exposes that header to the browser client, which is what lets the Angular client read the server-chosen name back (§8 of [Conversation Download](../ui/conversation-download.md#8-the-file-name)). `httpResponse.Headers.CacheControl = "no-store"` is set explicitly before the file is written, because a transcript is the most sensitive thing this API serves and the default caching behaviour for a static-looking file response is not "never write this to disk."

The route's OpenAPI metadata lists all five content types plus a 503 (`.ProducesProblem(StatusCodes.Status503ServiceUnavailable)`) beside the existing validation-problem and 404 declarations — matching the "declare every response with `.ProducesProblem`, never `.Produces<T>`" convention this codebase already follows elsewhere.

## 8. Configuration reference

| Setting | Default | Purpose |
| --- | --- | --- |
| `Export:DisabledFormats` | `[]` | Wire tokens (`md`, `docx`, `pdf`, `html`, `json`) to withdraw. Validated at startup against `ConversationExportFormatNames.Supported` |
| `Export:Pdf:FontDirectory` | `null` (→ `Fonts` beside the assembly) | Where `ExportFontResolver` looks first, before falling back to the operating system's own font directories |

## 9. Testing

Unit tests (`dotnet test --filter "Category!=Integration"`, no Docker):

| Area | Where |
| --- | --- |
| Format resolution, the registry, the 503 vs. 400 split, ownership | [`Services/ConversationExportServiceTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ConversationExportServiceTests.cs) |
| Which formats a deployment registers, given a template/font that is or is not present | [`Export/ExportRendererRegistrationTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/ExportRendererRegistrationTests.cs) |
| `?format=` token parsing and the supported-list message | [`Export/ExportFormatNamesTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/ExportFormatNamesTests.cs) |
| The Markdig walk: headings, lists, tables, code, nesting limits, image-to-alt-text | [`Export/MarkdownBlockMapperTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/MarkdownBlockMapperTests.cs) |
| The HTML block writer: every block kind, run styles, code fences, ragged tables | [`Export/HtmlBlockWriterTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/HtmlBlockWriterTests.cs) |
| `ExportTheme`: the template states no colour, face or size of its own, and references no variable the theme does not supply | [`Export/ExportThemeTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/ExportThemeTests.cs) |
| One fixture rendered to every format, and every block kind drawn identically by each renderer | [`Export/ExportParityTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/ExportParityTests.cs) |
| The font resolver: configured directory, platform fallback, all-or-nothing, bold-not-simulated | [`Export/ExportFontResolverTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/ExportFontResolverTests.cs) |
| PDF renderer, end to end against whatever fonts the host has | [`Export/PdfExportRendererTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/PdfExportRendererTests.cs) |
| Word renderer, including `OpenXmlValidator` schema validation, and the placeholder-substitution round trip | [`Services/ConversationExportServiceTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ConversationExportServiceTests.cs) |

**`OpenXmlValidator` is not a formality.** Running it against the generated `.docx` package caught four schema child-order bugs — elements appended in the order the code happened to build them rather than the order `CT_PPrBase`/`CT_RPr`/`CT_TblBorders` require — that a round-trip read through the same SDK had missed, because the SDK that wrote a malformed package is happy to read it back. Word itself is not that forgiving.

**Two PDF tests skip, deliberately, rather than fail, on a machine with no usable font.** `PdfFontFixture.SkipReason` (`Assert.SkipUnless(_fonts.IsUsable, …)`) is the "no font available" case this document describes throughout, made visible in the test output rather than hidden — a CI image or a developer machine without platform fonts installed sees a skip explaining why, not a false failure to chase.

At the point this shipped: **1274 unit tests and 284 integration tests** pass (up from 1152/250 before EP-15).

## 10. Known limits

- **`json` alone is unfiltered by any client surface.** It remains on the route exactly as before — nothing here changed its behaviour — and exists for scripted callers; the Angular client's download menu now offers the other four ([Conversation Download §1](../ui/conversation-download.md#1-overview)).
- **`docx`, `pdf` and `html` all lose images, keeping alt text.** Deliberate — see §5.2. Nothing is planned to revisit this; the SSRF trade is not one a per-deployment allowlist would meaningfully close, since model output chooses the URL.
- **PDF export needs a usable font, but no longer needs an operator to provide one.** Five faces are committed to the repository (§6.2, [`Export/Fonts/README.md`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Fonts/README.md)), so a bare `dotnet/aspnet` container now renders a PDF like any other deployment. The dependency did not go away — an operator who empties `Export:Pdf:FontDirectory`, or points it somewhere with no usable face, gets the same 503 as before — it is simply satisfied by default now instead of left for them to solve.
- **No export is cached.** Every request re-reads the transcript and re-renders. That matches the "response body, not blob storage" decision in §1: caching would need somewhere to put the cached artefact, and the whole point of not writing to blob storage was not needing one.

## 11. Key files

| Concern | File |
| --- | --- |
| Service, format resolution, ownership | [`Service/Export/ConversationExportService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportService.cs) |
| Format names and wire tokens | [`Service/Export/ConversationExportFormatNames.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportFormatNames.cs) |
| Renderer contract and reduced document view | [`Service/Export/IConversationExportRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/IConversationExportRenderer.cs), [`ConversationExportDocument.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportDocument.cs) |
| The five renderers | [`Service/Export/Renderers/HtmlExportRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/HtmlExportRenderer.cs), [`HtmlBlockWriter.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/HtmlBlockWriter.cs), [`JsonExportRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/JsonExportRenderer.cs), [`MarkdownExportRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/MarkdownExportRenderer.cs), [`WordExportRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/WordExportRenderer.cs), [`PdfExportRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Renderers/PdfExportRenderer.cs) |
| Block model and the Markdig walk | [`Service/Export/ExportBlocks.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportBlocks.cs), [`ExportBlockStyle.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportBlockStyle.cs), [`MarkdownBlockMapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/MarkdownBlockMapper.cs) |
| The shared theme (§6.1) | [`Service/Export/ExportTheme.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportTheme.cs) |
| Shared markdown pipeline | [`Service/Rendering/MarkdownPipelines.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownPipelines.cs) |
| Fonts | [`Service/Export/Fonts/ExportFontResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Fonts/ExportFontResolver.cs), [`Export/Fonts/README.md`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/Fonts/README.md) |
| Configuration | [`Service/Settings/ExportOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/ExportOptions.cs) |
| Startup registration and availability logging | [`Api/Export/ExportRendererRegistration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Export/ExportRendererRegistration.cs), [`ExportAvailabilityLogger.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Export/ExportAvailabilityLogger.cs) |
| The 503 exception and problem type | [`Service/Exceptions/ExportRendererNotConfiguredException.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ExportRendererNotConfiguredException.cs), [`Api/Problems/ProblemTypes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemTypes.cs) |
| Route | [`Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs) |
| Related reference | [Transcript Storage §11–§12](transcript-storage.md#11-server-rendered-html), [Conversation Download](../ui/conversation-download.md) (the client), [the rebuild PRD, US-1501](../prd/enterprise-ui-rebuild.md#us-1501-enabler-expose-conversation-export-over-http-b11) |
