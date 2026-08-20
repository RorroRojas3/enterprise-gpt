# Export fonts

PDF conversation export (US-1501) draws with the faces
[`ExportFontResolver`](ExportFontResolver.cs) finds. PDFsharp's cross-platform build ships no fonts
and reads none from the operating system on its own, so **a deployment with no usable face renders no
PDF** — the renderer is not registered and `GET api/conversations/{id}/export?format=pdf` answers
`503` with the `/problems/export-renderer-not-configured` type rather than failing mid-response.

## Getting a deterministic result

Drop TrueType or OpenType files into **this directory**, named by **role** rather than by typeface.
`Enterprise.Gpt.Service.csproj` copies `*.ttf`, `*.otf` and `*.ttc` from here into `Fonts/` beside the
built assembly, which is where the resolver probes; `Export:Pdf:FontDirectory` overrides that path:

| File | Used for |
| --- | --- |
| `export-sans-regular.ttf` | Body text. **Required** — without it there is no PDF renderer at all. |
| `export-sans-bold.ttf` | Headings, strong emphasis, table headers. |
| `export-sans-italic.ttf` | Emphasis. |
| `export-sans-bold-italic.ttf` | Both at once. |
| `export-mono-regular.ttf` | Code blocks and inline code. |

Only the first is required; every other role falls back along its own axis and ends at the regular
face. Bold is **never** simulated — PDFsharp documents its bold-simulation flag as unimplemented — so
a deployment without `export-sans-bold` renders bold text upright and unemboldened.

The app's own brand faces are [Inter](https://github.com/rsms/inter) (body) and
[JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) (code), both under the SIL Open Font
License, which permits redistribution. Copying `Inter-Regular.ttf`, `Inter-Bold.ttf`,
`Inter-Italic.ttf`, `Inter-BoldItalic.ttf` and `JetBrainsMono-Regular.ttf` here under the role names
above gives a PDF export that matches the UI.

> The `@fontsource/*` packages the frontend installs ship **`.woff`/`.woff2` only**, which PDFsharp
> cannot read. Take the `.ttf` files from the upstream releases instead.

## Doing nothing

If this directory is empty the resolver falls back to the operating system's own font directories,
looking for Inter, Segoe UI, DejaVu Sans, Liberation Sans, Noto Sans and Arial in that order (and the
matching monospace faces). That is what makes a developer machine render a PDF with nothing
provisioned — and it is also why output is only guaranteed to be identical across machines once the
files above are in place.

A container built `FROM mcr.microsoft.com/dotnet/aspnet` has **no fonts at all**. Either copy the
files here into the image, or install a font package (`fonts-dejavu-core` on Debian and Ubuntu,
`dejavu-sans-fonts` on Alpine and RHEL).

## Turning it off deliberately

`Export:DisabledFormats: [ "pdf" ]` unregisters the renderer whatever the fonts say, which is the
supported way to make the format unavailable rather than accidentally unavailable.
