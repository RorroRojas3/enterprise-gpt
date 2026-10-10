using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Enterprise.Gpt.Service.Rendering;

/// <summary>
/// The one Markdig configuration this solution parses message text with.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than duplicated because the input is attacker-influenceable: model output is shaped
/// by uploaded documents and MCP tool results, and a user prompt travels the same path. A second
/// pipeline built beside this one would be a second trust boundary that has to be got right twice —
/// and the export renderers are the case that proves it, since a <c>javascript:</c> URL is a live
/// click target inside a Word or PDF document exactly as it is inside a browser.
/// </para>
/// <para>
/// Built once at type initialization: assembling a <see cref="MarkdownPipeline"/> is per-process
/// configuration, not per-request work.
/// </para>
/// </remarks>
public static class MarkdownPipelines
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

    /// <summary>
    /// Gets the pipeline used for both HTML rendering and export parsing.
    /// </summary>
    public static MarkdownPipeline Default { get; } = Build();

    /// <summary>
    /// Parses markdown into its syntax tree with link schemes already restricted.
    /// </summary>
    /// <param name="markdown">The message text.</param>
    /// <returns>The parsed document.</returns>
    /// <remarks>
    /// The explicit <see cref="RestrictLinkSchemes"/> call is <em>redundant today and deliberate</em>.
    /// Verified against the pinned Markdig 1.3.2: <see cref="Markdown.Parse(string, MarkdownPipeline, MarkdownParserContext)"/>
    /// does raise <c>DocumentProcessed</c>, so the handler <see cref="Build"/> attaches has already
    /// stripped the disallowed URLs by the time this returns. It is called again because Markdig
    /// documents that event as part of the rendering pipeline rather than of parsing, and because the
    /// property being protected — that no caller walking this tree can reach a <c>javascript:</c>
    /// target — should hold from reading this method, not from a library behaviour that could change
    /// in a patch release. The cost is one tree walk per message against a render that lays out pages.
    /// </remarks>
    public static MarkdownDocument Parse(string markdown)
    {
        var document = Markdown.Parse(markdown, Default);
        RestrictLinkSchemes(document);

        return document;
    }

    /// <summary>
    /// Whether a link or image URL may be emitted.
    /// </summary>
    /// <param name="url">The URL as authored.</param>
    /// <returns><see langword="true"/> when the URL carries no scheme or an allowed one.</returns>
    public static bool IsAllowedUrl(string? url)
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

    private static MarkdownPipeline Build()
    {
        var builder = new MarkdownPipelineBuilder()
            // Raw HTML in the source is emitted as escaped text rather than markup, which is what
            // stops a <script> block or an <img onerror=…> in model output from becoming an element.
            .DisableHtml()
            .UsePipeTables()
            .UseAutoLinks()
            .UseTaskLists();

        // A composed email is fenced so a client can offer to open it in a mail client, not because
        // it is code. Rendered here rather than only in the export mapper, because the stored HTML is
        // what the HTML export re-serves and it would otherwise show the message as source.
        builder.Extensions.Add(new EmailFenceExtension());

        // DisableHtml does not touch link targets, so a javascript: href written as ordinary
        // markdown survives it. This is the pass that removes them.
        builder.DocumentProcessed += RestrictLinkSchemes;

        return builder.Build();
    }

    private sealed class EmailFenceExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
        }

        public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
        {
            if (renderer is HtmlRenderer html)
            {
                html.ObjectRenderers.Replace<CodeBlockRenderer>(new EmailFenceCodeBlockRenderer());
            }
        }
    }

    private sealed class EmailFenceCodeBlockRenderer : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            // The base renderer owns indented code blocks as well as fenced ones, so anything that
            // is not an email fence has to reach it untouched.
            if (obj is not FencedCodeBlock fenced || !EmailFence.Matches(fenced))
            {
                base.Write(renderer, obj);

                return;
            }

            renderer.EnsureLine();

            // Markup is skipped in plain-text mode, where escaping is off too — emitting these
            // elements there would put raw, unescaped tags into the output.
            if (!renderer.EnableHtmlForBlock)
            {
                foreach (var text in EmailFence.Lines(fenced))
                {
                    renderer.WriteLine(text);
                }

                return;
            }

            renderer.Write("<div class=\"email\">");

            foreach (var line in EmailFence.Lines(fenced))
            {
                // Escaped, not written raw: the fence's content is model output and the surrounding
                // pipeline's DisableHtml never sees inside a code block.
                renderer.Write("<p>").WriteEscape(line).Write("</p>");
            }

            renderer.WriteLine("</div>");
        }
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
}
