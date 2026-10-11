using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports;

/// <summary>
/// The <c>?format=</c> values the export route accepts, and the mapping to
/// <see cref="ConversationExportFormats"/>.
/// </summary>
/// <remarks>
/// One table, so the token a caller sends, the one a rejection lists and the
/// <c>export-renderer-not-configured</c> problem's <c>format</c> extension stay identical for clients.
/// </remarks>
public static class ConversationExportFormatNames
{
    /// <summary>
    /// The accepted values, in the order a rejection message lists them; a list because that order reaches
    /// users and dictionary order is unspecified.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["html", "json", "md", "docx", "pdf"];

    private static readonly Dictionary<string, ConversationExportFormats> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["html"] = ConversationExportFormats.Html,
            ["json"] = ConversationExportFormats.Json,
            ["md"] = ConversationExportFormats.Markdown,
            ["docx"] = ConversationExportFormats.Docx,
            ["pdf"] = ConversationExportFormats.Pdf
        };

    /// <summary>
    /// Gets the accepted values rendered for a validation message: <c>'html', 'json', …</c>.
    /// </summary>
    public static string SupportedList { get; } =
        string.Join(", ", Supported.Select(name => $"'{name}'"));

    #region Public static methods

    /// <summary>
    /// Resolves a <c>?format=</c> value; absent or blank means <see cref="ConversationExportFormats.Html"/>,
    /// which keeps a plain browser link working.
    /// </summary>
    public static bool TryParse(string? format, out ConversationExportFormats result)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            result = ConversationExportFormats.Html;

            return true;
        }

        return ByName.TryGetValue(format.Trim(), out result);
    }

    /// <summary>
    /// Renders a format as the token it travels as.
    /// </summary>
    public static string ToWireFormat(this ConversationExportFormats format) => format switch
    {
        ConversationExportFormats.Html => "html",
        ConversationExportFormats.Json => "json",
        ConversationExportFormats.Markdown => "md",
        ConversationExportFormats.Docx => "docx",
        ConversationExportFormats.Pdf => "pdf",
        _ => format.ToString().ToLowerInvariant()
    };

    #endregion
}
