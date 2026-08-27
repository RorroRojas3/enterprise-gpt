using Enterprise.Gpt.Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// Base type for one column of an ingested sheet: the name a query may address it by, and the value
/// kind inferred for it at ingestion.
/// </summary>
public class BaseDocumentSheetColumn : BaseModifiedEntity
{
    /// <summary>
    /// The column's zero-based position in the sheet, left to right.
    /// </summary>
    public int ColumnIndex { get; set; }

    /// <summary>
    /// The column's header text, or a generated <c>Column{n}</c> where the sheet has no header or
    /// the header cell is blank.
    /// </summary>
    /// <remarks>
    /// Unique within its sheet: duplicates are suffixed at ingestion, because a name that resolves
    /// to two columns is a query that cannot be answered.
    /// </remarks>
    [StringLength(256)]
    public string ColumnName { get; set; } = null!;

    /// <summary>
    /// The value kind inferred from a bounded sample of the column's own cells.
    /// </summary>
    public SheetColumnType InferredType { get; set; }
}
