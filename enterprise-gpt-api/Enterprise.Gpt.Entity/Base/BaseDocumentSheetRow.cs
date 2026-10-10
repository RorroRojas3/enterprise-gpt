namespace Enterprise.Gpt.Entity;

/// <summary>
/// Base type for one data row of an ingested sheet, holding its cells as a JSON object keyed by
/// column name.
/// </summary>
public class BaseDocumentSheetRow : BaseModifiedEntity
{
    /// <summary>
    /// The row's zero-based position among the sheet's data rows, header excluded.
    /// </summary>
    public int RowIndex { get; set; }

    /// <summary>
    /// The row's populated cells, as one JSON object of column name to cell text.
    /// </summary>
    /// <remarks>
    /// Mapped to SQL Server's native <c>json</c> type; the fallback for an engine without it is
    /// <c>nvarchar(max)</c> plus an <c>ISJSON</c> check constraint on this column alone. Empty cells
    /// are omitted rather than stored blank, so a wide sparse sheet does not repeat every column
    /// name on every row.
    /// </remarks>
    public string Cells { get; set; } = null!;
}
