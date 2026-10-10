using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// One matching row of a filter result.
/// </summary>
public sealed class SheetQueryRow
{
    /// <summary>
    /// Gets or sets the row's zero-based position among the sheet's data rows.
    /// </summary>
    [JsonPropertyOrder(0)]
    public int RowIndex { get; set; }

    /// <summary>
    /// Gets or sets the row's cells, keyed by column name. A column the row leaves empty is absent.
    /// </summary>
    [JsonPropertyOrder(1)]
    public IReadOnlyDictionary<string, string> Cells { get; set; } = new Dictionary<string, string>();
}
