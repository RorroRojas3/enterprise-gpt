using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.Documents;

/// <summary>
/// One data row of a sheet.
/// </summary>
/// <param name="Cells">A JSON object of column name to cell text, carrying only populated cells.</param>
public sealed record SheetRowDto(int RowIndex, string Cells);
