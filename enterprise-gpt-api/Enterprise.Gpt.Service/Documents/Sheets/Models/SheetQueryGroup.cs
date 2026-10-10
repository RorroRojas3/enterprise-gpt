using System.ComponentModel;
using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// One group of a grouped aggregate.
/// </summary>
public sealed class SheetQueryGroup
{
    /// <summary>
    /// Gets or sets the group's value of the grouping column, or <see langword="null"/> for the rows
    /// with no value in it.
    /// </summary>
    [JsonPropertyOrder(0)]
    public string? Group { get; set; }

    /// <summary>
    /// Gets or sets the aggregate computed for this group.
    /// </summary>
    [JsonPropertyOrder(1)]
    public object? Value { get; set; }

    /// <summary>
    /// Gets or sets how many rows fell into this group.
    /// </summary>
    [JsonPropertyOrder(2)]
    public int RowCount { get; set; }
}
