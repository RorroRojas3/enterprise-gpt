using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Documents.Sheets.Models;

/// <summary>
/// The result of one <c>sheet_query</c> call, serialized straight back to the model.
/// </summary>
/// <remarks>
/// A refusal is a successful result carrying <see cref="Note"/> and whichever of
/// <see cref="AvailableSheets"/> and <see cref="AvailableColumns"/> lets the model correct itself,
/// never an exception: a sheet or column that does not exist is something the model can fix on its
/// next call.
/// </remarks>
public sealed class SheetQueryResult
{
    /// <summary>
    /// Gets or sets the operation that was asked for, echoed as supplied so the model can tell
    /// several calls apart.
    /// </summary>
    [JsonPropertyOrder(0)]
    public required string Operation { get; set; }

    /// <summary>
    /// Gets or sets the file the queried sheet belongs to.
    /// </summary>
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentName { get; set; }

    /// <summary>
    /// Gets or sets the sheet that was queried.
    /// </summary>
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SheetName { get; set; }

    /// <summary>
    /// Gets or sets the column the aggregate was computed over.
    /// </summary>
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Column { get; set; }

    /// <summary>
    /// Gets or sets the column the aggregate was grouped by.
    /// </summary>
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupBy { get; set; }

    /// <summary>
    /// Gets or sets the computed value of an ungrouped aggregate: a number for a numeric column, the
    /// cell's own text for anything else.
    /// </summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    /// <summary>
    /// Gets or sets how many rows matched the filters: every one of them for a value or a row listing,
    /// and the rows in the groups shown for a grouped one.
    /// </summary>
    [JsonPropertyOrder(6)]
    public int MatchedRowCount { get; set; }

    /// <summary>
    /// Gets or sets one entry per distinct value of the grouping column.
    /// </summary>
    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SheetQueryGroup>? Groups { get; set; }

    /// <summary>
    /// Gets or sets the matching rows of a filter result.
    /// </summary>
    [JsonPropertyOrder(8)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SheetQueryRow>? Rows { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether rows or groups were dropped to stay inside a cap, so
    /// the model knows a narrower question would show more.
    /// </summary>
    [JsonPropertyOrder(9)]
    public bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets how many matching rows contributed nothing to the aggregate, because the cell was
    /// empty or its value could not be read as the column's own type.
    /// </summary>
    /// <remarks>
    /// A column's inferred type comes from a bounded sample, so a stray cell can disagree with it.
    /// Reporting the count is what keeps a total that skipped rows from reading as a complete one.
    /// </remarks>
    [JsonPropertyOrder(10)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedValues { get; set; }

    /// <summary>
    /// Gets or sets the sheets that could have been queried, each qualified by its file. Populated
    /// only when a sheet could not be identified.
    /// </summary>
    [JsonPropertyOrder(11)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AvailableSheets { get; set; }

    /// <summary>
    /// Gets or sets the resolved sheet's own columns with their inferred types. Populated only when a
    /// column could not be identified.
    /// </summary>
    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AvailableColumns { get; set; }

    /// <summary>
    /// Gets or sets a short explanation of a refused or unusual result, for the model rather than the
    /// user.
    /// </summary>
    [JsonPropertyOrder(13)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether nothing was queried because a name did not resolve.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Note"/>: a query that ran perfectly well also carries one when it matched no rows,
    /// hit a cap, or read a column whose values disagree with its type.
    /// </remarks>
    [JsonIgnore]
    public bool IsRefusal { get; set; }
}
