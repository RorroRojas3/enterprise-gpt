using System.ComponentModel;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// One row filter as the model supplies it, before any of it is validated.
/// </summary>
/// <remarks>
/// Every member is nullable and untyped on purpose: a missing or unrecognised part is answered with a
/// refusal naming what is actually available, not with a deserialization failure the model cannot read.
/// </remarks>
public sealed class SheetFilter
{
    /// <summary>
    /// Gets or sets the column to compare.
    /// </summary>
    [Description("The name of the column to compare, exactly as it appears in the sheet.")]
    public string? Column { get; set; }

    /// <summary>
    /// Gets or sets the comparison to apply.
    /// </summary>
    [Description("One of: eq, neq, gt, gte, lt, lte, contains.")]
    public string? Comparator { get; set; }

    /// <summary>
    /// Gets or sets the value to compare against.
    /// </summary>
    [Description("The value to compare against, as text. It is converted to the column's own type before comparison.")]
    public string? Value { get; set; }
}
