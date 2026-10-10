namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The inline styling an <see cref="ExportRun"/> carries.
/// </summary>
/// <remarks>
/// Flags rather than an enum because markdown nests emphasis freely — <c>**bold _and italic_**</c> is
/// one run wearing two styles, not a choice between them.
/// </remarks>
[Flags]
public enum ExportRunStyles
{
    /// <summary>Plain body text.</summary>
    None = 0,

    /// <summary>Strong emphasis.</summary>
    Bold = 1,

    /// <summary>Emphasis.</summary>
    Italic = 2,

    /// <summary>Inline code, rendered in the monospace face.</summary>
    Code = 4
}
