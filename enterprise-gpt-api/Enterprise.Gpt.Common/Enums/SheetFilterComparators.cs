namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// How one filter compares a column against a value. Closed by design, for the same reason
/// <see cref="SheetQueryOperations"/> is.
/// </summary>
public enum SheetFilterComparators
{
    Eq = 1,

    /// <summary>Not equal to, which a row with no value in that column also satisfies.</summary>
    Neq = 2,

    Gt = 3,

    Gte = 4,

    Lt = 5,

    Lte = 6,

    /// <summary>Contains, as a case- and accent-insensitive substring of the cell's text.</summary>
    Contains = 7
}
