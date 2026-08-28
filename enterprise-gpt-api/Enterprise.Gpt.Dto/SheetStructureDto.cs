using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto;

/// <summary>
/// One worksheet's structured cell data, produced alongside the text segments it was read into.
/// </summary>
/// <remarks>
/// A <c>.csv</c> is modelled as a single-sheet workbook, so both formats persist and are queried
/// through the same shape.
/// </remarks>
/// <param name="SheetIndex">The sheet's 1-based position, matching the segments' source number.</param>
/// <param name="RowCount">Data rows, header excluded.</param>
public sealed record SheetStructureDto(
    int SheetIndex,
    string SheetName,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<SheetColumnDto> Columns,
    IReadOnlyList<SheetRowDto> Rows);

/// <summary>
/// One column of a sheet, named as a later query may address it.
/// </summary>
/// <param name="ColumnName">Unique within its sheet: a duplicate header is suffixed at extraction.</param>
public sealed record SheetColumnDto(int ColumnIndex, string ColumnName, SheetColumnType InferredType);

/// <summary>
/// One data row of a sheet.
/// </summary>
/// <param name="Cells">A JSON object of column name to cell text, carrying only populated cells.</param>
public sealed record SheetRowDto(int RowIndex, string Cells);
