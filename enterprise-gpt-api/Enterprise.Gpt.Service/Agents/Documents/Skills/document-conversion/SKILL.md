---
name: document-conversion
description: Convert a document between supported formats at the fidelity the confirmed matrix records. Use for any convert request.
---

# Converting between formats

Every pair below was attempted against this sandbox. The tier is what you may promise, and the tier
decides what your answer has to say.

- **✓** faithful — nothing the source expressed is lost. No caveat needed.
- **◐** structural — content, headings, tables and lists survive; pagination, typography and
  vendor layout do not. Serve it, and name what was lost in one sentence. A structural tier is a
  conversion to perform, never a reason to decline.
- **refused** — no path here produces something worth handing over. Refuse by name **before running any
  code**, say why in one clause, and suggest a supported alternative when there is one.
- **n/a** — not offered. Not a refusal; no natural request maps to it.

<!-- conversion-matrix:begin -->
Status: confirmed. Recorded: 2026-08-28. Deployment: rr-gpt-5.6-luna. Office converter: absent.

| From \ To | docx | xlsx | pptx | pdf | csv | md | txt |
| --- | --- | --- | --- | --- | --- | --- | --- |
| docx | — | n/a | n/a | ◐ | n/a | ✓ | ✓ |
| xlsx | n/a | — | n/a | ◐ | ✓ | ✓ | ✓ |
| pptx | n/a | n/a | — | ◐ | n/a | ✓ | ✓ |
| pdf | ◐ | ◐ | refused | — | ◐ | ◐ | ◐ |
| csv | ✓ | ✓ | n/a | ◐ | — | ✓ | ✓ |
| md | ✓ | n/a | n/a | ✓ | n/a | — | ✓ |
| txt | ✓ | n/a | n/a | ✓ | n/a | ✓ | — |
<!-- conversion-matrix:end -->


## Recipes

| Pair | How |
| --- | --- |
| Office to `pdf` | Read with `python-docx` / `openpyxl` / `python-pptx` and compose with `reportlab`, which is what produced every confirmed cell here; `weasyprint` failed with an `AttributeError` on this image. There is no `soffice` either, which is why these are structural rather than faithful. |
| `pdf` to `docx` | `fitz` (PyMuPDF 1.26.6) for text, headings and images, written out with `python-docx`. An editable document with the source's content, not a reconstruction of its layout. |
| `pdf` to `xlsx` / `csv` | `pdfplumber` 0.6.2 table extraction. Dependable for ruled tables, unreliable for whitespace-aligned ones. Report how many tables you found. |
| `pdf` to `md` / `txt` | `fitz` text extraction. Legible for a text PDF, empty for a scanned one — say which you got. |
| Anything to `md` / `txt` | Extract text in reading order and write it out. Formatting, images and layout are lost; say so. |
| `csv` / `xlsx` to `docx` | `pandas` to read, `python-docx` to write the table. |
| `md` / `txt` to anything | There is no Markdown parser here — walk the lines and map them yourself. See the `markdown-text` skill. |

## Before you run anything

1. Look the pair up above. A refused pair costs no sandbox time — refuse it.
2. If the named source matches more than one available file, ask which. If it matches none, say so and
   name what is available.
3. Otherwise convert, then verify the result with the `artifact-verification` skill before answering.
