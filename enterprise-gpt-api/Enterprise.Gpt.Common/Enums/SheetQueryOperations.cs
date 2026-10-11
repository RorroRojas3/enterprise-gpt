namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// What a <c>sheet_query</c> call asks for. Closed by design: the model chooses from this set and
/// never supplies an expression of its own.
/// </summary>
public enum SheetQueryOperations
{
    /// <summary>Total of a column's numeric values.</summary>
    Sum = 1,

    /// <summary>Mean of a column's numeric values.</summary>
    Avg = 2,

    Min = 3,

    Max = 4,

    /// <summary>Number of matching rows, or of rows with a value in a named column.</summary>
    Count = 5,

    /// <summary>The matching rows themselves rather than a computed value.</summary>
    Filter = 6
}
