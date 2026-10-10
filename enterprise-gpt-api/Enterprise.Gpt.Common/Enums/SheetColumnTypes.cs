namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The value kind inferred for a spreadsheet column from a bounded sample of its own cells.
/// </summary>
/// <remarks>
/// Inference is a hint for how a column is described and later compared, not a constraint on what a
/// cell may hold: a column whose sampled cells disagree is <see cref="Text"/>, and every cell is
/// stored as the text the reader sees either way.
/// </remarks>
public enum SheetColumnType
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
