using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Enterprise.Gpt.Service.Conversations.Rendering;

/// <summary>
/// The one Markdig configuration this solution parses message text with.
/// </summary>
/// <remarks>
/// Shared because the input is attacker-influenceable (model output, documents, MCP results, prompts) and
/// the export renderers need the same defences: a <c>javascript:</c> URL is live in a Word or PDF too.
/// </remarks>
public static class MarkdownPipelines
{
    /// <summary>
    /// The URL schemes a rendered link or image may carry.
    /// </summary>
    /// <remarks>
    /// An allowlist, since a blocklist is wrong the moment a browser accepts a scheme not on it. Relative
    /// URLs carry no scheme and are always allowed.
    /// </remarks>
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>
    /// Gets the pipeline used for both HTML rendering and export parsing.
    /// </summary>
    public static MarkdownPipeline Default { get; } = Build();

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
            // The base renderer also owns indented blocks, so non-email blocks reach it untouched.
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

    #region Private methods

    private static MarkdownPipeline Build()
    {
        var builder = new MarkdownPipelineBuilder()
            // Escapes raw HTML, so a <script> or <img onerror=…> in model output never becomes an element.
            .DisableHtml()
            .UsePipeTables()
            .UseAutoLinks()
            .UseTaskLists();

        // Rendered here, not only in the export mapper, because the HTML export re-serves the stored
        // HTML and would otherwise show the email as source.
        builder.Extensions.Add(new EmailFenceExtension());

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

    #endregion

    #region Public static methods

    /// <summary>
    /// Parses markdown into its syntax tree with link schemes already restricted.
    /// </summary>
    /// <remarks>
    /// <see cref="RestrictLinkSchemes"/> runs again deliberately: Markdig 1.3.2 raises
    /// <c>DocumentProcessed</c> on parse but documents it as a rendering event, so do not rely on it.
    /// </remarks>
    public static MarkdownDocument Parse(string markdown)
    {
        var document = Markdown.Parse(markdown, Default);
        RestrictLinkSchemes(document);

        return document;
    }

    /// <summary>Whether a link or image URL may be emitted: it carries no scheme, or an allowed one.</summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        // Browsers ignore control characters inside a scheme, so "java\0script:" must be read as
        // "javascript:" before the allowlist check.
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

    #endregion
}
