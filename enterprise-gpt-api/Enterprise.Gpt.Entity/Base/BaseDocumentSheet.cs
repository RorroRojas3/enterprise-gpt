namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// Base type for one worksheet of an ingested spreadsheet: its name, its shape, and the anchor its
/// columns and rows hang off.
/// </summary>
/// <remarks>
/// A document holds many sheets, so uniqueness is per ordinal rather than per document.
/// </remarks>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseDocumentSheet<TKey> : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <summary>
    /// The sheet's 1-based position in the workbook's own tab order, and the value a chunk drawn
    /// from it carries as <see cref="BaseDocumentChunk{TKey}.SourceNumber"/>.
    /// </summary>
    public int SheetIndex { get; set; }

    /// <summary>
    /// The sheet's name as the workbook declares it, or the file name for a <c>.csv</c>.
    /// </summary>
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
