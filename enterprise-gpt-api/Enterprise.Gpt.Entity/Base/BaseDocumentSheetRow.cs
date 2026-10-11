namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// Base type for one data row of an ingested sheet, holding its cells as a JSON object keyed by
/// column name.
/// </summary>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseDocumentSheetRow<TKey> : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <summary>
    /// The row's zero-based position among the sheet's data rows, header excluded.
    /// </summary>
    public int RowIndex { get; set; }

    /// <summary>
    /// The row's populated cells, as one JSON object of column name to cell text.
    /// </summary>
    /// <remarks>
    /// Empty cells are omitted rather than stored blank, so a wide sparse sheet does not repeat every
    /// column name on every row.
    /// </remarks>
    public string Cells { get; set; } = null!;
}
