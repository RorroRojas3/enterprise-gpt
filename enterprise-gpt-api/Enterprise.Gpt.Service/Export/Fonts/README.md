# Export fonts

PDF conversation export draws with the faces [`ExportFontResolver`](ExportFontResolver.cs) finds.
PDFsharp's cross-platform build ships no fonts and reads none from the operating system on its own,
so **a deployment with no usable face renders no PDF** — the renderer is not registered and
`GET api/conversations/{id}/export?format=pdf` answers `503` with the
`/problems/export-renderer-not-configured` type rather than failing mid-response.

## What ships

Five faces are committed to this directory, named by **role** rather than by typeface, and
`Enterprise.Gpt.Service.csproj` copies `*.ttf`, `*.otf` and `*.ttc` from here into `Fonts/` beside the
built assembly, which is where the resolver probes first:

| File | Typeface | Used for |
| --- | --- | --- |
| `export-sans-regular.ttf` | Inter Regular | Body text. **Required** — without it there is no PDF renderer at all. |
| `export-sans-bold.ttf` | Inter Bold | Headings, strong emphasis, table headers. |
| `export-sans-italic.ttf` | Inter Italic | Emphasis. |
| `export-sans-bold-italic.ttf` | Inter Bold Italic | Both at once. |
| `export-mono-regular.ttf` | JetBrains Mono Regular | Code blocks and inline code. |

They are the application's own brand faces, so a PDF export matches the UI, and both are under the
SIL Open Font License, which permits redistribution — `OFL-Inter.txt` and `OFL-JetBrainsMono.txt` are
the upstream notices the licence requires travel with them. Take replacements from the upstream
releases: the `@fontsource/*` packages the frontend installs ship `.woff`/`.woff2` only, which
PDFsharp cannot read.

Because these ship, **PDF export works in a bare `mcr.microsoft.com/dotnet/aspnet` container** with no
font package installed, and the output is identical on every machine.

## Re-branding a deployment

Replace the five files above, or point `Export:Pdf:FontDirectory` at a directory holding files under
the same role names. Only `export-sans-regular` is required; every other role falls back along its own
axis and ends at the regular face. Bold is **never** simulated — PDFsharp documents its
bold-simulation flag as unimplemented — so a directory without `export-sans-bold` renders bold text
upright and unemboldened.

The two sources are all-or-nothing rather than merged per role: if the configured directory supplies
no regular face, the resolver falls back to the operating system's own font directories, looking for
Inter, Segoe UI, DejaVu Sans, Liberation Sans, Noto Sans and Arial in that order (and the matching
monospace faces). That fallback is what makes an emptied directory still render something, not a way
to fill one missing weight from the platform — a document set half in one typeface and half in
another is worse than one unemboldened heading.

Colours and sizes are not here. They come from
[`ExportTheme`](../ExportTheme.cs), which the HTML and Word renderers read too.

## Turning it off deliberately

`Export:DisabledFormats: [ "pdf" ]` unregisters the renderer whatever the fonts say, which is the
supported way to make the format unavailable rather than accidentally unavailable.
