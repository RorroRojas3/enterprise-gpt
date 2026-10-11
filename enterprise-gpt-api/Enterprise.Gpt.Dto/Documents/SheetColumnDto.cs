using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.Documents;

/// <summary>
/// One column of a sheet, named as a later query may address it.
/// </summary>
/// <param name="ColumnIndex">The column's 0-based position in the sheet, which orders the columns.</param>
/// <param name="ColumnName">Unique within its sheet: a duplicate header is suffixed at extraction.</param>
/// <param name="InferredType">The type sampled from the column's populated cells in the first 200 data rows; <see cref="SheetColumnTypes.Text"/> when they disagree or none is populated.</param>
public sealed record SheetColumnDto(int ColumnIndex, string ColumnName, SheetColumnTypes InferredType);
