using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// A column of a sheet the caller may query, resolved from its stored column row.
/// </summary>
internal sealed record ResolvedSheetColumn(string Name, SheetColumnTypes Type);
