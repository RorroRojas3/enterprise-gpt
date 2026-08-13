using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Net;

namespace Enterprise.Gpt.Service.Rendering;

/// <summary>
/// Renders markdown to HTML safe to store and serve.
/// </summary>
public interface IMarkdownRenderer
{
    /// <summary>
    /// Renders <paramref name="markdown"/> to HTML.
    /// </summary>
    /// <param name="markdown">The markdown to render.</param>
    /// <returns>The rendered HTML.</returns>
    string Render(string? markdown);
}

/// <summary>
/// Renders with Markdig, with raw HTML passthrough disabled and link schemes restricted.
/// </summary>
/// <remarks>
/// <para>
/// Model output is attacker-influenceable — through uploaded documents and MCP tool results — so
/// what is rendered here is untrusted input, not the platform's own copy. Raw HTML is escaped
/// rather than emitted, which neutralizes markup smuggled through either route.
/// </para>
/// <para>
/// Registered as a singleton: building the pipeline is per-process configuration, not per-request
/// work. The per-call renderer is separate because Markdig's <see cref="HtmlRenderer"/> holds
/// per-render state and is not thread-safe.
/// </para>
/// </remarks>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    /// <summary>
    /// The URL schemes a rendered link may carry.
    /// </summary>
    /// <remarks>
    /// Markdig does not filter schemes, so <c>javascript:</c> would otherwise survive into a
    /// stored artifact. Relative and fragment links carry no scheme and pass.
    /// </remarks>
    private static readonly string[] _allowedSchemes = ["http", "https", "mailto"];

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <inheritdoc />
    public string Render(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var document = Markdig.Markdown.Parse(markdown, _pipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsAllowedUrl(link.Url))
            {
                link.Url = string.Empty;
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return writer.ToString();
    }

    private static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        // Decoded first: an entity-encoded scheme still executes once a browser parses the href.
        var candidate = WebUtility.HtmlDecode(url).TrimStart();
        var separator = candidate.IndexOf(':');
        if (separator < 0)
        {
            return true;
        }

        // A colon appearing after a slash or a question mark belongs to a path or a query rather
        // than to a scheme, so the link is relative and safe.
        var slash = candidate.AsSpan(0, separator).IndexOfAny('/', '?', '#');
        if (slash >= 0)
        {
            return true;
        }

        var scheme = candidate[..separator];

        return _allowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }
}
