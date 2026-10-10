using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// Base type for one worksheet of an ingested spreadsheet: its name, its shape, and the anchor its
/// columns and rows hang off.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="BaseDocumentChunk"/>'s split — an unmapped base plus one mapped table per
/// document family — because the two document families are structurally identical and differ only
/// in which document they hang off.
/// </para>
/// <para>
/// A document holds many sheets, so uniqueness is per ordinal rather than per document.
/// </para>
/// </remarks>
public class BaseDocumentSheet : BaseModifiedEntity
{
    /// <summary>
    /// The sheet's 1-based position in the workbook's own tab order, and the value a chunk drawn
    /// from it carries as <see cref="BaseDocumentChunk.SourceNumber"/>.
    /// </summary>
    public int SheetIndex { get; set; }

    /// <summary>
    /// The sheet's name as the workbook declares it, or the file name for a <c>.csv</c>.
    /// </summary>
    [StringLength(256)]
    public string SheetName { get; set; } = null!;

    /// <summary>
    /// How many data rows the sheet yielded, excluding its header row.
    /// </summary>
    public int RowCount { get; set; }

    /// <summary>
    /// How many columns the sheet yielded, counted to its widest populated cell.
    /// </summary>
    public int ColumnCount { get; set; }
}
