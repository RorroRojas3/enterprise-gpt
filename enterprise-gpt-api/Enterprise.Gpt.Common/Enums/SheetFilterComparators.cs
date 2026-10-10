namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// How one filter compares a column against a value. Closed by design, for the same reason
/// <see cref="SheetQueryOperations"/> is.
/// </summary>
public enum SheetFilterComparators
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
