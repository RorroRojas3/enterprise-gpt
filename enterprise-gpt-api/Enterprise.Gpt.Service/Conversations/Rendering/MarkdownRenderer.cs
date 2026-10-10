using System.Net;
using Markdig;
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
/// Registered as a singleton, though the pipeline it renders through is
/// <see cref="MarkdownPipelines.Default"/> and is shared with the export renderers.
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
            return Markdown.ToHtml(markdown, MarkdownPipelines.Default);
        }
        catch (Exception ex)
        {
            // Length only: the content is a user prompt or a model answer, and neither belongs in a
            // log. The length is what distinguishes a pathological input from an ordinary one.
            _logger.LogError(ex, "Rendering a {Length}-character message to HTML failed; storing escaped plain text instead.", markdown.Length);

            return WebUtility.HtmlEncode(markdown);
        }
    }
}
