namespace Enterprise.Gpt.Dto.Documents;

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
