namespace Enterprise.Gpt.Dto.Documents;

/// <summary>
/// One worksheet's structured cell data, produced alongside the text segments it was read into.
/// </summary>
/// <remarks>
/// A <c>.csv</c> is modelled as a single-sheet workbook, so both formats persist and are queried
/// through the same shape.
/// </remarks>
/// <param name="SheetIndex">The sheet's 1-based position, matching the segments' source number.</param>
/// <param name="SheetName">The worksheet's name, or a <c>.csv</c>'s file name without its extension; <c>Sheet</c> plus its position when blank.</param>
/// <param name="RowCount">Data rows, header excluded.</param>
/// <param name="ColumnCount">The widest populated row's cell count, header included, and so the length of <paramref name="Columns"/>.</param>
/// <param name="Columns">Every column in sheet order, each uniquely named, including those the header left blank.</param>
/// <param name="Rows">The populated data rows in reading order, header excluded.</param>
public sealed record SheetStructureDto(
    int SheetIndex,
    string SheetName,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<SheetColumnDto> Columns,
    IReadOnlyList<SheetRowDto> Rows);
