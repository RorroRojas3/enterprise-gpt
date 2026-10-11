namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The value kind inferred for a spreadsheet column from a bounded sample of its own cells.
/// </summary>
/// <remarks>
/// A hint for description and comparison, not a constraint: a column whose sampled cells disagree is
/// <see cref="Text"/>, and every cell is stored as the text the reader sees.
/// </remarks>
public enum SheetColumnTypes
{
    /// <summary>Free text, and the fallback for a mixed or empty column.</summary>
    Text = 1,

    /// <summary>Every sampled cell parses as a number.</summary>
    Number = 2,

    /// <summary>Every sampled cell parses as a date or a time.</summary>
    Date = 3,

    /// <summary>Every sampled cell reads as true or false.</summary>
    Boolean = 4
}
