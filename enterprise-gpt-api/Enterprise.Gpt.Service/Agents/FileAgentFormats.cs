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

    /// <summary>
    /// The words that name a target format, deliberately narrower than the skill filter's own topics.
    /// </summary>
    /// <remarks>
    /// "document" and "sheet" are absent on purpose: both read as common nouns far more often than as a
    /// format, and a wrong reading here refuses a conversion rather than advertising an unwanted skill.
    /// </remarks>
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

    /// <summary>Splits a string into the distinct words a topic or format can be matched against.</summary>
    /// <param name="text">The instruction, or <see langword="null"/>.</param>
    /// <returns>The words, compared case-insensitively.</returns>
    public static HashSet<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                text.Split(_wordBreaks, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>Strikes a set of file names out of an instruction, leaving the prose around them.</summary>
    /// <param name="instruction">The run's instruction.</param>
    /// <param name="names">The names to remove.</param>
    /// <returns>The instruction with every occurrence of every name replaced by a space.</returns>
    /// <remarks>
    /// A file name splits into ordinary words — "draft.docx" yields "draft" — so a scan reading an
    /// instruction for what it asks for has to strike the names out before it counts anything.
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

    /// <summary>
    /// Finds the formats an instruction asks for, ignoring the source files it names.
    /// </summary>
    /// <param name="instruction">The run's instruction.</param>
    /// <param name="sourceNames">
    /// The file names already resolved as sources. Removed before the scan, because "convert deck.pptx
    /// to pdf" otherwise reads its own source as a second target and no request would ever name exactly
    /// one.
    /// </param>
    /// <returns>The distinct target formats named, in the order <see cref="Producible"/> lists them.</returns>
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
}
