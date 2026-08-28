---
name: pdf-authoring
description: Produce PDFs with reportlab, fpdf2 or weasyprint. Use when producing a .pdf.
---

# Authoring .pdf

Installed: `reportlab` 4.4.5, `fpdf` **2.8.3 — this is the fpdf2 API**, `weasyprint` 53.3. There is **no** LibreOffice or Word in this image, so a PDF made here uses the sandbox's own fonts and will not match a Word export's typography. Say that once, in your answer.

## reportlab — structured documents

```python
from reportlab.lib.pagesizes import LETTER
from reportlab.lib.styles import getSampleStyleSheet
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle
from reportlab.lib import colors

styles = getSampleStyleSheet()
story = [Paragraph("Quarterly Review", styles["Title"]), Spacer(1, 12)]
story.append(Paragraph("Prepared for the leadership team.", styles["BodyText"]))

table = Table([["Metric", "Value"], ["Revenue", "$4.1M"]])
table.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), colors.lightgrey),
    ("GRID", (0, 0), (-1, -1), 0.5, colors.grey),
]))
story.append(table)

SimpleDocTemplate("/mnt/data/quarterly-review.pdf", pagesize=LETTER).build(story)
```

## weasyprint — when you already have HTML

```python
from weasyprint import HTML
HTML(string=html).write_pdf("/mnt/data/report.pdf")
```

Good for content that is naturally a document — headings, tables, lists. **It failed with an `AttributeError` on every Office-to-PDF attempt recorded against this image**, so reach for it second: `reportlab` is what produced every confirmed structural conversion.

## fpdf2 — short, simple pages

```python
from fpdf import FPDF

pdf = FPDF()
pdf.add_page()
pdf.set_font("helvetica", size=12)
pdf.cell(0, 10, "Quarterly Review", new_x="LMARGIN", new_y="NEXT")
pdf.output("/mnt/data/quarterly-review.pdf")
```

`new_x`/`new_y` are the 2.x API. `ln=1`, from fpdf 1.x, is deprecated here.

## Notes

- The built-in fonts are Helvetica, Times and Courier, and they are Latin-1 only. For anything outside that range use reportlab or weasyprint.
- No web font, logo or template can be fetched — the sandbox has no network.
