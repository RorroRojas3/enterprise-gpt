using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.Documents;

/// <summary>
/// One column of a sheet, named as a later query may address it.
/// </summary>
/// <param name="ColumnName">Unique within its sheet: a duplicate header is suffixed at extraction.</param>
public sealed record SheetColumnDto(int ColumnIndex, string ColumnName, SheetColumnTypes InferredType);
