---
name: csv-tabular
description: Produce and reshape tabular data with pandas. Use for .csv output and for tables feeding other formats.
---

# Tabular data and .csv

`pandas` 1.5.3 and `tabulate` 0.9.0 are installed.

## Write

```python
import pandas as pd

frame = pd.DataFrame(rows, columns=["Region", "Revenue", "Growth"])
frame.to_csv("/mnt/data/regional-summary.csv", index=False)
```

`index=False` is not optional — a stray index column is the defect that makes a generated CSV misalign in every consumer.

## Read

```python
frame = pd.read_csv("/mnt/data/<input>.csv")
```

If the file may not be UTF-8, pass `encoding="utf-8", encoding_errors="replace"`. If the delimiter is unclear, `sep=None, engine="python"` sniffs it.

## Notes

- Keep the header row: a CSV without one is not usable by the tools people take it to.
- Write dates ISO-8601 (`frame["date"].dt.strftime("%Y-%m-%d")`) rather than in a locale format.
- `DataFrame.to_markdown()` works here because `tabulate` is installed, but a CSV request wants `to_csv`.
