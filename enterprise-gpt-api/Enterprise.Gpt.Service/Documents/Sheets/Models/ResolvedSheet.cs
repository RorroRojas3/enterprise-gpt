using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// A sheet the caller may query, resolved from the turn's document scope.
/// </summary>
/// <param name="Id">The persisted sheet's id, which a query is scoped to.</param>
/// <param name="DocumentId">The conversation or project document the sheet was read from.</param>
/// <param name="Source">Whether that document belongs to the conversation or to its project, which keeps the two sheet tables' ids apart.</param>
/// <param name="DocumentName">The workbook's file name, echoed on every query result.</param>
/// <param name="SheetName">The worksheet's name, which a caller's sheet reference is matched against case-insensitively.</param>
/// <param name="Label">
/// The sheet qualified by its document, in the shape a retrieval citation already uses, so a sheet
/// name that repeats across two workbooks is still nameable.
/// </param>
/// <param name="RowCount">Data rows, header excluded.</param>
/// <param name="Columns">The sheet's columns in sheet order, which a query's column references resolve against case-insensitively.</param>
internal sealed record ResolvedSheet(
    Guid Id,
    Guid DocumentId,
    DocumentSources Source,
    string DocumentName,
    string SheetName,
    string Label,
    int RowCount,
    IReadOnlyList<ResolvedSheetColumn> Columns);
