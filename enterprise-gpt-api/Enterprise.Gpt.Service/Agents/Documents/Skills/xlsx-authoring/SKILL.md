---
name: xlsx-authoring
description: Write and edit Excel workbooks with openpyxl. Use when producing or editing an .xlsx.
---

# Authoring .xlsx

`openpyxl` 3.1.5 and `pandas` 1.5.3 are installed.

## Create

```python
from openpyxl import Workbook
from openpyxl.styles import Font
from openpyxl.utils import get_column_letter

wb = Workbook()
ws = wb.active
ws.title = "Summary"

ws.append(["Region", "Revenue", "Growth"])
for cell in ws[1]:
    cell.font = Font(bold=True)

for row in (("North", 1200000, 0.12), ("South", 940000, 0.04)):
    ws.append(row)

ws.column_dimensions[get_column_letter(1)].width = 18
ws.freeze_panes = "A2"

wb.save("/mnt/data/regional-summary.xlsx")
```

## From a DataFrame

```python
import pandas as pd

frame = pd.DataFrame(rows, columns=["Region", "Revenue"])
with pd.ExcelWriter("/mnt/data/regional-summary.xlsx", engine="openpyxl") as writer:
    frame.to_excel(writer, sheet_name="Summary", index=False)
```

`index=False` matters — a stray index column is the most common defect in a generated sheet.

## Notes

- Number formats are strings on the cell: `cell.number_format = "#,##0"` or `"0.0%"`.
- Formulas are written as text (`ws["D2"] = "=B2*C2"`); openpyxl does not evaluate them, so never read a computed value back out of a file you just wrote.
- Multiple sheets: `wb.create_sheet("Detail")`.
