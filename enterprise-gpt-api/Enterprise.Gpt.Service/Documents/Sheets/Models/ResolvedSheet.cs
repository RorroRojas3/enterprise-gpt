using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// A sheet the caller may query, resolved from the turn's document scope.
/// </summary>
/// <param name="Id">The persisted sheet id a query is scoped to.</param>
/// <param name="DocumentId">The conversation or project document the sheet was read from.</param>
/// <param name="Source">Keeps the two sheet tables' ids apart.</param>
/// <param name="DocumentName">The workbook's file name, echoed on every query result.</param>
/// <param name="SheetName">Matched case-insensitively against the caller's sheet reference.</param>
/// <param name="Label">
/// Qualified by its document, as citations show it, so a name repeated across workbooks stays nameable.
/// </param>
/// <param name="RowCount">Data rows, header excluded.</param>
/// <param name="Columns">In sheet order; column references resolve case-insensitively.</param>
internal sealed record ResolvedSheet(
    Guid Id,
    Guid DocumentId,
    DocumentSources Source,
    string DocumentName,
    string SheetName,
    string Label,
    int RowCount,
    IReadOnlyList<ResolvedSheetColumn> Columns);
