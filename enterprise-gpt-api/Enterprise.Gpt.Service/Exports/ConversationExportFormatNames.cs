namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// The <c>?format=</c> values the export route accepts, and the mapping to
/// <see cref="ConversationExportFormats"/>.
/// </summary>
/// <remarks>
/// One table rather than a parse method and a separate display string: the value a caller sends, the
/// value named in a rejection's supported-set message, and the value carried on the
/// <c>export-renderer-not-configured</c> problem's <c>format</c> extension all have to be the same
/// token, or a client matching on one of them matches nothing.
/// </remarks>
public static class ConversationExportFormatNames
{
    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    /// <remarks>
    /// Declared as a list rather than read back from the lookup's keys: this order reaches users, in
    /// the <c>detail</c> of every 400 the route emits, and <c>Dictionary</c> makes no promise about
    /// enumeration order.
    /// </remarks>
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

    /// <summary>
    /// Resolves a <c>?format=</c> value.
    /// </summary>
    /// <param name="format">The requested value, or <see langword="null"/> when the caller sent none.</param>
    /// <param name="result">The resolved format.</param>
    /// <returns><see langword="true"/> when the value names a supported format.</returns>
    /// <remarks>
    /// An absent or blank value resolves to <see cref="ConversationExportFormats.Html"/>, which is what
    /// the route has always done and what keeps a plain browser link working.
    /// </remarks>
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
    /// <param name="format">The format.</param>
    /// <returns>The wire token, such as <c>docx</c>.</returns>
    public static string ToWireFormat(this ConversationExportFormats format) => format switch
    {
        ConversationExportFormats.Html => "html",
        ConversationExportFormats.Json => "json",
        ConversationExportFormats.Markdown => "md",
        ConversationExportFormats.Docx => "docx",
        ConversationExportFormats.Pdf => "pdf",
        _ => format.ToString().ToLowerInvariant()
    };
}
