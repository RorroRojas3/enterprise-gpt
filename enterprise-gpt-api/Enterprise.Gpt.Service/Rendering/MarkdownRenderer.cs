using System.Net;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Rendering;

/// <summary>
/// Renders a message's markdown to the HTML stored beside it.
/// </summary>
public interface IMarkdownRenderer
{
    /// <summary>
    /// Renders markdown to HTML safe to store and re-serve.
    /// </summary>
    /// <param name="markdown">The message text.</param>
    /// <returns>
    /// The rendered HTML, or the HTML-escaped input when rendering fails. Empty input renders to an
    /// empty string.
    /// </returns>
    /// <remarks>
    /// Never throws. A rendering fault must not lose a turn whose answer has already been streamed
    /// to the user, so a failure degrades to escaped plain text and is logged.
    /// </remarks>
    string Render(string? markdown);
}

/// <summary>
/// Renders markdown with Markdig, with raw HTML passthrough disabled and link schemes restricted.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton: building a <see cref="MarkdownPipeline"/> is per-process
/// configuration, not per-request work.
/// </para>
/// <para>
/// The input is attacker-influenceable. Model output is shaped by uploaded documents and MCP tool
/// results, and a user prompt is rendered through this same path, so this is a real trust boundary
/// rather than a formatting convenience.
/// </para>
/// </remarks>
/// <param name="logger">The logger.</param>
public sealed class MarkdownRenderer(ILogger<MarkdownRenderer> logger) : IMarkdownRenderer
{
    /// <summary>
    /// The URL schemes a rendered link or image may carry.
    /// </summary>
    /// <remarks>
    /// An allowlist rather than a blocklist of the known-dangerous ones: <c>javascript:</c> is only
    /// the best-known of them, and a blocklist is wrong the moment a browser accepts something not
    /// on it. A relative URL carries no scheme at all and is always allowed.
    /// </remarks>
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private readonly ILogger<MarkdownRenderer> _logger = logger;

    /// <inheritdoc />
    public string Render(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        try
        {
            return Markdown.ToHtml(markdown, Pipeline);
        }
        catch (Exception ex)
        {
            // Length only: the content is a user prompt or a model answer, and neither belongs in a
            // log. The length is what distinguishes a pathological input from an ordinary one.
            _logger.LogError(ex, "Rendering a {Length}-character message to HTML failed; storing escaped plain text instead.", markdown.Length);

            return WebUtility.HtmlEncode(markdown);
        }
    }

    private static MarkdownPipeline BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            // Raw HTML in the source is emitted as escaped text rather than markup, which is what
            // stops a <script> block or an <img onerror=…> in model output from becoming an element.
            .DisableHtml()
            .UsePipeTables()
            .UseAutoLinks()
            .UseTaskLists();

        // DisableHtml does not touch link targets, so a javascript: href written as ordinary
        // markdown survives it. This is the pass that removes them.
        builder.DocumentProcessed += RestrictLinkSchemes;

        return builder.Build();
    }

    private static void RestrictLinkSchemes(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsAllowedUrl(link.Url))
            {
                link.Url = string.Empty;
            }
        }

        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsAllowedUrl(link.Url))
            {
                link.Url = string.Empty;
            }
        }
    }

    private static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        // Control characters are stripped before the scheme is read: browsers ignore them inside a
        // scheme, so "java\0script:" and "java\nscript:" reach the same handler as the plain form
        // while a naive comparison sees a scheme that is on no list.
        var candidate = new string([.. url.Where(character => !char.IsControl(character))]).Trim();

        var separator = candidate.IndexOf(':', StringComparison.Ordinal);

        if (separator < 0)
        {
            return true;
        }

        // A colon after a path separator is part of the path, not a scheme — "foo/bar:baz" is a
        // relative URL and must not be read as the "foo/bar" scheme.
        var delimiter = candidate.AsSpan(0, separator).IndexOfAny('/', '?', '#');

        return delimiter >= 0 || AllowedSchemes.Contains(candidate[..separator]);
    }
}
