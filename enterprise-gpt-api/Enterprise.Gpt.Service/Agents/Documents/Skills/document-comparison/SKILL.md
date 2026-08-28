---
name: document-comparison
description: Compare two documents and report what differs. Use when asked what changed between two files.
---

# Comparing two documents

Read both, reduce each to comparable text, and diff. Produce a comparison **file** only when one was asked for.

## Extract comparable text

| Format | How |
| --- | --- |
| `.docx` | `[p.text for p in Document(path).paragraphs]` |
| `.pptx` | every `shape.text_frame.text` on every slide, in slide order |
| `.pdf` | `fitz.open(path)` then `page.get_text()` per page |
| `.xlsx` / `.csv` | `pandas` frames, compared as data rather than as text |
| `.md` / `.txt` | the file's lines |

## Diff

```python
import difflib

diff = list(difflib.unified_diff(before, after, lineterm="", n=2))
added = sum(1 for line in diff if line.startswith("+") and not line.startswith("+++"))
removed = sum(1 for line in diff if line.startswith("-") and not line.startswith("---"))
```

For spreadsheets, compare structure first and values second:

```python
only_in_before = set(before.columns) - set(after.columns)
changed = before.compare(after) if before.shape == after.shape else None
```

`DataFrame.compare` requires identical shapes and labels; when they differ, report the shape difference rather than forcing an alignment.

## Reporting

- Lead with the shape of the change: how many sections, rows or slides were added, removed and modified.
- Then name the substantive differences, not every whitespace change.
- If one of the two files could not be read, say **which one**. Never compare against nothing and present the result as a comparison.
