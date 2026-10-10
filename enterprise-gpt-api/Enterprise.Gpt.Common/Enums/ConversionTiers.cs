using System.Text.Json;

namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// How much of a source survives a conversion, as the confirmed matrix records it.
/// </summary>
public enum ConversionTiers
{
    /// <summary>Not proposed for this version. Not a refusal — no natural request maps to the pair.</summary>
    NotOffered = 0,

    /// <summary>Nothing the source expressed is lost. No caveat needed.</summary>
    Faithful = 1,

    /// <summary>
    /// Content, headings, tables and lists survive; pagination, typography and vendor layout do not.
    /// A conversion to serve with its caveat, never a reason to decline.
    /// </summary>
    Structural = 2,

    /// <summary>No path in this sandbox produces a result worth handing over.</summary>
    Refused = 3
}
