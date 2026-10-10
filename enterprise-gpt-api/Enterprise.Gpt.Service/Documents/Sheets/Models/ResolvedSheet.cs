using System.ComponentModel;
using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// A sheet the caller may query, resolved from the turn's document scope.
/// </summary>
/// <param name="Label">
/// The sheet qualified by its document, in the shape a retrieval citation already uses, so a sheet
/// name that repeats across two workbooks is still nameable.
/// </param>
internal sealed record ResolvedSheet(
    Guid Id,
    Guid DocumentId,
    DocumentSources Source,
    string DocumentName,
    string SheetName,
    string Label,
    int RowCount,
    IReadOnlyList<ResolvedSheetColumn> Columns);
