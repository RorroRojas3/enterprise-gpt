---
name: artifact-verification
description: Re-open a produced file and assert it parses and matches the request. Run before answering.
---

# Verifying what you produced

Deterministic checks only. No judgement — open the file, measure it, print the result.

```python
import json
import os


def verify(path, kind):
    result = {"path": path, "bytes": os.path.getsize(path)}
    if result["bytes"] == 0:
        result["ok"] = False
        return result

    if kind == "docx":
        from docx import Document
        doc = Document(path)
        result.update(paragraphs=len(doc.paragraphs), tables=len(doc.tables))
    elif kind == "xlsx":
        from openpyxl import load_workbook
        wb = load_workbook(path)
        result.update(sheets=len(wb.sheetnames), names=wb.sheetnames)
    elif kind == "pptx":
        from pptx import Presentation
        result.update(slides=len(Presentation(path).slides))
    elif kind == "pdf":
        import fitz
        with fitz.open(path) as pdf:
            result.update(pages=pdf.page_count)
    elif kind == "csv":
        import pandas as pd
        frame = pd.read_csv(path)
        result.update(rows=len(frame), columns=list(frame.columns))
    else:
        result.update(characters=len(open(path, encoding="utf-8").read()))

    result["ok"] = True
    return result


print(json.dumps(verify("/mnt/data/<file>", "<kind>")))
```

## What counts as passing

- The file opens with the library that owns its format.
- It is not zero bytes.
- Its shape matches what was asked for: sheet count, slide count, page count, or a parseable header row.

## When it fails

Fix the generating code and run it again. Never answer with a file that did not verify, and never describe a file you could not re-open.
