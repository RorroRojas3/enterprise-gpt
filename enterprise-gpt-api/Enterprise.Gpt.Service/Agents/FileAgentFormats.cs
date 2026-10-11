using System.Collections.Frozen;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// Reads an instruction for the formats it names.
/// </summary>
/// <remarks>
/// Matching is on whole words: "md" occurs inside "command" and "text" inside "context", and a token
/// that matches everything is worse than one that matches nothing.
/// </remarks>
public static class FileAgentFormats
{
    /// <summary>The formats this platform produces, in the matrix's own order.</summary>
    public static readonly IReadOnlyList<string> Producible = ["docx", "xlsx", "pptx", "pdf", "csv", "md", "txt"];

    private static readonly char[] _wordBreaks =
    [
        ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'',
        '/', '\\', '*', '_', '`'
    ];

    // Narrower than the skill filter's topics: "document" and "sheet" are usually common nouns, and a wrong
    // reading here refuses a conversion.
    private static readonly FrozenDictionary<string, string> _byWord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["docx"] = "docx",
        ["word"] = "docx",
        ["xlsx"] = "xlsx",
        ["excel"] = "xlsx",
        ["spreadsheet"] = "xlsx",
        ["workbook"] = "xlsx",
        ["pptx"] = "pptx",
        ["powerpoint"] = "pptx",
        ["deck"] = "pptx",
        ["slides"] = "pptx",
        ["presentation"] = "pptx",
        ["pdf"] = "pdf",
        ["csv"] = "csv",
        ["md"] = "md",
        ["markdown"] = "md",
        ["txt"] = "txt"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    #region Public static methods

    /// <summary>Splits a string into the distinct words a topic or format can be matched against.</summary>
    /// <returns>The words, compared case-insensitively.</returns>
    public static HashSet<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                text.Split(_wordBreaks, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>Strikes a set of file names out of an instruction, leaving the prose around them.</summary>
    /// <remarks>
    /// A file name splits into ordinary words ("draft.docx" yields "draft"), so scans strike names first.
    /// </remarks>
    public static string Without(string instruction, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var remainder = instruction;

        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                remainder = remainder.Replace(name, " ", StringComparison.OrdinalIgnoreCase);
            }
        }

        return remainder;
    }

    /// <summary>Finds the target formats an instruction names, in <see cref="Producible"/> order.</summary>
    /// <remarks>
    /// Resolved source names are struck out first, or "convert deck.pptx to pdf" reads its source as a target.
    /// </remarks>
    public static IReadOnlyList<string> DetectTargets(string? instruction, IEnumerable<string> sourceNames)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);

        if (string.IsNullOrWhiteSpace(instruction))
        {
            return [];
        }

        var words = Words(Without(instruction, sourceNames));

        return
        [
            .. Producible.Where(format => _byWord.Any(
                pair => string.Equals(pair.Value, format, StringComparison.Ordinal) && words.Contains(pair.Key)))
        ];
    }

    #endregion
}
