---
name: docx-authoring
description: Write and edit Word documents with python-docx. Use when producing or editing a .docx.
---

# Authoring .docx

`python-docx` 1.2.0 is installed. `docx2pdf`, `mammoth` and `pandoc` are not, and nothing can be installed.

## Create

```python
from docx import Document
from docx.shared import Inches

doc = Document()
doc.add_heading("Quarterly Review", level=0)
doc.add_paragraph("Prepared for the leadership team.")

doc.add_heading("Highlights", level=1)
for line in ("Revenue up 12%", "Two new markets", "Churn flat"):
    doc.add_paragraph(line, style="List Bullet")

table = doc.add_table(rows=1, cols=2)
table.style = "Light Grid Accent 1"
header = table.rows[0].cells
header[0].text, header[1].text = "Metric", "Value"
for metric, value in (("Revenue", "$4.1M"), ("Churn", "2.1%")):
    row = table.add_row().cells
    row[0].text, row[1].text = metric, value

doc.save("/mnt/data/quarterly-review.docx")
```

## Edit an existing document

Open it, change it, save under a **new** name. Never overwrite the source.

```python
doc = Document("/mnt/data/<input>.docx")
for paragraph in doc.paragraphs:
    if "TBD" in paragraph.text:
        for run in paragraph.runs:
            run.text = run.text.replace("TBD", "2026-Q1")
doc.save("/mnt/data/quarterly-review-updated.docx")
```

Editing a run rather than the paragraph is what preserves the paragraph's formatting; assigning to `paragraph.text` throws it away.

## Notes

- `add_heading(level=0)` is the title style; levels 1-9 are headings.
- A style name that is not in the default template raises `KeyError`. Stick to the built-in names above.
- Images: `doc.add_picture(path, width=Inches(6))`, and only from a file already in `/mnt/data`.
