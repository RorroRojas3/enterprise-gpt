---
name: markdown-text
description: Produce .md and .txt output, and parse Markdown without the markdown package. Use for plain-text targets.
---

# Markdown and plain text

**The `markdown` package is NOT installed in this image, and nothing can be installed.** Any recipe importing it fails. `beautifulsoup4` 4.14.3 and `lxml` 6.1.1 are present for the HTML direction.

## Write

```python
from pathlib import Path

Path("/mnt/data/summary.md").write_text(text, encoding="utf-8")
```

Always write UTF-8 explicitly; the default encoding is not guaranteed to be what a reader expects.

## Composing Markdown

Build it as text. A table is just rows:

```python
def table(headers, rows):
    lines = ["| " + " | ".join(headers) + " |",
             "| " + " | ".join("---" for _ in headers) + " |"]
    lines += ["| " + " | ".join(str(cell) for cell in row) + " |" for row in rows]
    return chr(10).join(lines)
```

`pandas.DataFrame.to_markdown()` also works — `tabulate` is installed.

## Markdown into another format

There is no Markdown parser here. Walk the source line by line and map it: `#` prefixes to heading levels, `-`/`*` prefixes to list items, `|` rows to table rows, everything else to a paragraph. That is what the conversion recipes do, and it is why a Markdown conversion is honest about losing inline formatting.

## Notes

- `.txt` output is the same write with the formatting stripped: no `#`, no `|`, no `*`.
- Preserve blank lines between blocks. Markdown that renders as one paragraph is the usual symptom of dropping them.
