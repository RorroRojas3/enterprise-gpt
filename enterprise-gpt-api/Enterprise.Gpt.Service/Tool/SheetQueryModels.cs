using Enterprise.Gpt.Common.Enums;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// What a <c>sheet_query</c> call asks for. Closed by design: the model chooses from this set and
/// never supplies an expression of its own.
/// </summary>
public enum SheetQueryOperation
{
    /// <summary>Total of a column's numeric values.</summary>
    Sum = 1,

    /// <summary>Mean of a column's numeric values.</summary>
    Avg = 2,

    /// <summary>Smallest value in a column.</summary>
    Min = 3,

    /// <summary>Largest value in a column.</summary>
    Max = 4,

    /// <summary>Number of matching rows, or of rows with a value in a named column.</summary>
    Count = 5,

    /// <summary>The matching rows themselves rather than a computed value.</summary>
    Filter = 6
}

/// <summary>
/// How one filter compares a column against a value. Closed by design, for the same reason
/// <see cref="SheetQueryOperation"/> is.
/// </summary>
public enum SheetFilterComparator
{
    /// <summary>Equal to.</summary>
    Eq = 1,

    /// <summary>Not equal to, which a row with no value in that column also satisfies.</summary>
    Neq = 2,

    /// <summary>Greater than.</summary>
    Gt = 3,

    /// <summary>Greater than or equal to.</summary>
    Gte = 4,

    /// <summary>Less than.</summary>
    Lt = 5,

    /// <summary>Less than or equal to.</summary>
    Lte = 6,

    /// <summary>Contains, as a case- and accent-insensitive substring of the cell's text.</summary>
    Contains = 7
}

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

/// <summary>
/// A column of a sheet the caller may query, resolved from its stored column row.
/// </summary>
internal sealed record ResolvedSheetColumn(string Name, SheetColumnType Type);

/// <summary>
/// A sheet the caller may query, resolved from the turn's document scope.
/// </summary>
/// <param name="Label">
/// The sheet qualified by its document, in the shape a retrieval citation already uses, so a sheet
/// name that repeats across two workbooks is still nameable.
/// </param>
internal sealed record ResolvedSheet(
    Guid Id,
    Guid DocumentId,
    DocumentSource Source,
    string DocumentName,
    string SheetName,
    string Label,
    int RowCount,
    IReadOnlyList<ResolvedSheetColumn> Columns);

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
}
