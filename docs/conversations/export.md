# Export

Turning a conversation into a downloadable file, in five formats, and the one special block type the
transcript carries.

## Formats and the renderer registry

`ExportRendererRegistration.AddExportRenderers` runs once at composition and decides which of the
five `IConversationExportRenderer` implementations are registered:

| Format | Registered unless | Extra requirement |
| --- | --- | --- |
| `json`, `md`, `docx` | named in `Export:DisabledFormats` | none |
| `html` | same | `Files/conversation-history.html` exists on disk |
| `pdf` | same | `ExportFontResolver` finds a usable face |

Both extra requirements are checked **at composition, not on first use**. The HTML template check
would otherwise surface as a `TypeInitializationException` on the first export of *any* format, and
no handler arm maps that to anything but a bare 500. The PDF font resolver is assigned to
PDFsharp's process-global `GlobalFontSettings.FontResolver`, which is documented as set-once before
any font operation — so "can this deployment render a PDF?" is a startup answer rather than a
per-request surprise.

Five font faces ship under `Export/Fonts/`, so a bare `mcr.microsoft.com/dotnet/aspnet` container
renders a PDF with no font package installed.

`Export:DisabledFormats` is validated at startup against the same token list the route accepts, so a
typo fails app start rather than silently withdrawing nothing — the bargain
`PermissionEndpointFilter.Require` makes with a bad permission id.

Registration overwrites by indexer rather than `ToDictionary`: `ToDictionary` throws on a duplicate
key, and that throw would land in the scoped service's constructor on *every* export request, giving
a 400 with a LINQ exception message in the body.

`ExportAvailabilityLogger`, a hosted service, reports the outcome once at startup and re-derives
*why* a format is missing. Absent and named in `DisabledFormats` means withdrawn on purpose; absent
and unnamed means it could not be built, which logs at `Warning` because every entry is a route that
will answer 503 to a request nobody configured it to refuse.

### The 503

`ExportRendererNotConfiguredException` derives from `Exception`, **not** `InvalidOperationException`
— the global handler maps the latter to 400, which would report a deployment gap as a client mistake
and send the caller into a retry loop over a state no retry can change. It maps to 503 under
`/problems/export-renderer-not-configured`, carrying a `format` extension so a client offering four
formats can disable the unavailable one rather than the whole download control.

This is why format resolution is two steps: `TryParse` alone decides 400 (the token names nothing
this API ever supported), and the registry lookup after it decides 503 (the token names something
this API supports and this deployment does not offer). "Which formats does the API accept" and
"which does this deployment offer" are a contract and an environment, not one question.

## Reading versus re-rendering

`json` and `md` **read** what persist time already computed. `docx`, `pdf` and `html` **re-render**
through one shared path:

```
message.Markdown -> MarkdownPipelines.Parse -> MarkdownBlockMapper.Map -> IReadOnlyList<ExportBlock>
                                                        |            |             |
                                            WordExportRenderer  PdfExportRenderer  HtmlBlockWriter
```

`html` could have read the transcript's stored `htmlContent` and gives that up deliberately: it
would be the one format built from a different rendering of the same message, and an export that can
disagree with itself is worse than the cost of one more format walking the block model.

### The block model

`ExportBlock` is deliberately smaller than markdown: `HeadingBlock`, `ParagraphBlock`, `CodeBlock`,
`QuoteBlock`, `ListBlock`, `TableBlock`, `ThematicBreakBlock`, and inline `ExportRun` — a span
carrying bold/italic/code flags and an optional link. There is **no image block**.

`ExportBlockStyle` is a `[Flags]` enum (`Prompt`, `Quote`) each renderer threads through its own
walk rather than a property on the block: what encloses a block is something only the renderer
walking the tree knows.

Building the model once and handing the *same* one to all three renderers is what makes it
structurally impossible for a Word, PDF and HTML export of one conversation to disagree about what a
message's markdown meant.

`MarkdownBlockMapper.Map` never throws — a message that fails to parse degrades to a single
paragraph holding its own text rather than vanishing from the export. Two depth limits (`MaxDepth`
16 before a container is flattened, `MaxFlattenDepth` 32 before even that stops recursing) exist
because flattening is itself recursive and none of this input is trusted; a `StackOverflowException`
is not catchable.

### Images are dropped, alt text is kept

The mapper reads an image's alt text as ordinary runs and nothing else — no rendered format ever
sees the URL. Embedding the image would mean the API making an outbound request to a URL that
*model output chose*, from the API's own network position: a server-side request forgery surface
with no legitimate purpose, since a transcript export preserves what was said, not what was linked.

### One pipeline, one trust boundary

`MarkdownPipelines.Parse` is the single Markdig configuration this solution parses message text
with, and `MarkdownBlockMapper` calls that same `Parse`. So the link-scheme allowlist (`http`,
`https`, `mailto`; everything else blanked, including `javascript:`) and the `DisableHtml`
raw-HTML refusal that protect the stored transcript protect an export identically. A `javascript:`
URL is a live click target inside Word and most PDF readers exactly as in a browser.

### Shared appearance

`ExportTheme` is the single source of colours, faces and sizes for Word, PDF and HTML, so a
conversation exported three ways does not look like three unrelated documents.

## Composed email

When the user asks the assistant to write an email **they are sending**, the system prompt asks for
the finished email in a fence marked `email`, containing nothing else:

````markdown
```email
To: alice@contoso.com
Subject: Q3 budget review

Hi Alice,

Thursday's numbers are in and the totals moved. Could we push the review to Friday morning?

Thanks,
Priya
```
````

Header rules are narrow on purpose: only `To:`, `Cc:` and `Subject:`, in that order, each on its own
line, ended by a blank line. A stray `Bcc:` or `From:` ends the header run and everything after is
body. `Subject:` is always written, so a mail client never opens on a blank subject.

**Recipient provenance is the rule that does the real work.** `To:` and `Cc:` may only carry an
address the user typed themselves, in this conversation. An address that surfaces in pasted text, an
uploaded document, a search result or a tool result is data the model is reporting on, not a
recipient it may act on. Otherwise anything the model reads could steer who a one-click **Open
Email** control addresses a message to. An email with no `To:` is the ordinary case, not a failure.

The address the model does write is written in full, unredacted, unlike the general PII rules above
it in the prompt: it is a send target, not a quotation, and a redacted address opens a mail client
addressed to nobody.

**The fence is matched twice, and both sides must agree.** `EmailFence.Info` is `"email"`, matched
case-insensitively and only when the whole info string is exactly that word — Markdig splits at the
first space, so ` ```email draft ` has arguments and is left an ordinary code block. The client
reaches the same outcome by a different route in `domain/markdown/fences.ts`. The two never call
into each other; a divergence would render the same message as prose in an export and as source code
in the chat, which is why the info-string edge cases are pinned explicitly in tests. Tilde fences
match too.

The two sides read the fence's *content* differently on purpose: the server yields one entry per
non-blank line, because an export only has to reproduce readable prose; the client reads it
structurally, because it has to build a `mailto:` link needing recipient and subject as distinct
fields.

## Configuration

```jsonc
"Export": {
  "DisabledFormats": [],       // e.g. [ "pdf" ] to withdraw PDF deliberately
  "Pdf": { "FontDirectory": null }   // defaults to "Fonts" beside the built assembly
}
```

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportService.cs` | Format resolution, 400 versus 503 |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Export/ExportRendererRegistration.cs` | Which renderers this deployment gets |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Export/MarkdownBlockMapper.cs` | Markdown AST to the block model |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Export/ExportTheme.cs` | Shared palette and type scale |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/EmailFence.cs` | The server half of the fence contract |
| `enterprise-gpt-ui/src/app/domain/email/` | `mailto:` construction from the same fence |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/MarkdownBlockMapperTests.cs` | Info-string and nesting edge cases |

## Related

- [transcripts.md](transcripts.md)
- [../frontend/answer-rendering.md](../frontend/answer-rendering.md)
