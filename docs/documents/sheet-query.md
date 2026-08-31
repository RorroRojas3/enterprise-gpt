# Sheet Query

`sheet_query`: deterministic aggregation and filtering over ingested spreadsheets, so a question
about a number is answered by SQL rather than by a model reading chunks.

## The tool contract

```
sheet_query(operation, sheetName?, column?, groupBy?, filters?)
```

| Argument | Notes |
| --- | --- |
| `operation` | `sum`, `avg`, `min`, `max`, `count`, `filter`. The four aggregates need `column`; `count` accepts one optionally; `filter` ignores `column`/`groupBy` and returns whole rows. |
| `sheetName` | Matched exact, then prefix, then substring, case-insensitively, against both the bare sheet name and its document-qualified label. Omit to use the sole sheet in scope, or to discover what is attached. |
| `column` | The column to aggregate. Required for the four aggregates. |
| `groupBy` | Exactly one column. There is no multi-column `GROUP BY`. |
| `filters` | Up to `SheetQuery:MaxFilters` (default 5), all of which must hold. Each is `{ column, comparator, value }` with `comparator` in `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`. |

### Resolving names

A sheet is matched against its bare name *and* its `"{fileName} — {sheetName}"` label — the same
shape a retrieval citation uses. The label is what lets two workbooks in one conversation each hold
a `Sheet1` and still be told apart. Matching widens one rung at a time and stops at the first rung
producing exactly one result.

A **column** is matched case-insensitively and exactly against the sheet's stored column rows. There
is no widening, because a column name has no useful "close enough".

### Refusal, not widening

**An unknown or ambiguous sheet or column comes back as a successful result carrying `note` and a
listing of what exists — never an exception.** This is the opposite of `document_search`, which
widens an ambiguous name to a search over everything: a wider search is cheap, and a near-miss
reading as "the documents say nothing" is the worse failure there. `sheet_query` cannot take that
trade, because computing a total over the wrong sheet is a wrong number with nothing about it to
reveal the mistake.

```json
{
  "operation": "sum",
  "note": "No sheet is named 'Regional Rev'. Ask again with one of the available names.",
  "availableSheets": [
    "q3-budget.xlsx — Regional Revenue (4213 rows): SKU (text), Region (text), Revenue (number), Quarter (text)",
    "q3-budget.xlsx — Headcount (86 rows): Department (text), FTE (number), Location (text)"
  ]
}
```

An unresolved column carries `availableColumns` instead, scoped to the sheet that did resolve.

The listing is bounded — the first 20 sheets and 24 columns, each with a trailing count — because a
refusal is the one reply no configured cap reaches otherwise, and a 200-column sheet's names would
otherwise land in the turn's context by themselves.

**Discovery**: omitting `sheetName` with more than one sheet attached does not guess. It returns
every sheet with its row count and columns. The prompt tells the model to call the tool with no
sheet name first, which is cheaper than a `document_search` against the schema-card chunk.

## Why the SQL is hand-written, and how it stays safe

A model, not an engineer, supplies the sheet name, the column name and the filter values, so every
statement `SheetQuerySql` builds has to be safe by construction rather than by review.

- **A column reaches a statement only as its already-resolved `SheetColumnType`**, choosing between
  a small fixed set of pre-authored fragments. The model never supplies an expression; it supplies a
  name, resolved against the sheet's stored columns *before* any SQL is built.
- **A column's name travels only as a bound JSON-path parameter — `$.` never appears in command
  text at all.** A test asserts no generated statement contains the literal substring `$.`, which is
  the only route by which a model-named column could reach a command text.
- **A column name that cannot be expressed as a JSON path is refused by name, not escaped.** SQL
  Server documents no escaping rule for a quoted JSON member name, so a `"`, a `\`, or a control
  character is refused outright rather than guessed at.
- **Ownership is re-established in the statement, not trusted from the resolution pass.** The
  derived table every statement selects from takes `@sheetId`, `@conversationId` and `@projectId`
  and re-joins from the owning row, so a `sheetId` resolved for one caller cannot be replayed
  against another turn's parameters.

Soft delete is filtered by hand at row, sheet and owning-document level, in **both** arms of the
`UNION ALL` — six predicates, each pinned by test. A missing one would silently resurrect a deleted
row into a real answer.

Only the **number** of filter arms and the **presence** of a `groupBy` clause vary with the request
— never a value.

## Reading a cell

Every cell is stored as a JSON *string*, whatever the column's inferred type, so reading one back as
a number or a date always needs a conversion. The design uses `TRY_CONVERT` over the value
`JSON_VALUE` returns, rather than `JSON_VALUE(..., RETURNING float)`, for three reasons:

1. Every stored value is text, so `RETURNING` is a coercion either way.
2. `RETURNING` is supported only against the native `json` column type, and `Cells` has a documented
   fallback to `nvarchar(max)`. Depending on it would make this tool the thing that breaks that
   fallback.
3. `RETURNING` has no non-throwing form. One cell disagreeing with its column's sampled type would
   fail the whole aggregate instead of being reported as a skipped value.

`float` rather than `decimal` for a numeric read: it matches the arithmetic the spreadsheet itself
used, never overflows a `SUM` the way a fixed-precision `decimal` can, and does not silently drop a
value written in scientific notation. `Boolean` has no `RETURNING` type at all, so it compares as
the literal text it is stored as.

Two reads that surprise people:

- **A numeric read strips a comma group separator before converting.** Column-type inference accepts
  `1,200.50` as a number, but `TRY_CONVERT` alone returns `NULL` for a string containing a comma —
  so every comma-formatted export would silently read as unreadable rather than as the number it
  visibly is.
- **A time-only `Date` column anchors to 1900-01-01 on both sides of a comparison.**
  `TRY_CONVERT(datetime2, '14:30:00')` returns `1900-01-01 14:30:00` server-side, and a filter value
  the model supplies is re-anchored to the same date before binding. Without that, every time-only
  comparison would silently miss.

## `skippedValues`

`MatchedRowCount` counts every row matching the filters; `skippedValues` counts how many of those
contributed nothing to the aggregate — an empty cell, or one whose text could not be read as the
column's inferred type. It is `matchedRows - valueRows`, computed in the same statement.

Reported rather than swallowed, because a total that silently skipped a fifth of its matching rows
is not the same fact as one computed over all of them, and the difference is invisible in the number
alone. The field is omitted entirely when nothing was skipped, and means nothing for `count` or a
`Text` column.

## Configuration — `SheetQuery`

Hot-reloadable through `ISheetQueryOptionsProvider`. `MaxFilters` defaults to 5; the remaining keys
bound result size and row windows.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/SheetQueryService.cs` | Name resolution, refusals, result assembly |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/SheetQuerySql.cs` | Statement construction and its safety rules |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/SheetQueryTool.cs` | The model-facing declaration |
| `enterprise-gpt-api/Enterprise.Gpt.Common/Enums/SheetColumnType.cs` | The type catalog the expressions switch on |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Tool/SheetQuerySqlTests.cs` | No literals, no JSON path, all six soft-delete predicates |

## Related

- [ingestion.md](ingestion.md)
- [retrieval.md](retrieval.md)
