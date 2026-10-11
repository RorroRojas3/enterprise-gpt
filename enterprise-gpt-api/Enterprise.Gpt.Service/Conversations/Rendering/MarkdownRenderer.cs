using System.Net;
using Markdig;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Conversations.Rendering;

/// <summary>
/// Renders a message's markdown to the HTML stored beside it.
/// </summary>
public interface IMarkdownRenderer
{
    /// <summary>
    /// Renders markdown to HTML safe to store and re-serve; empty input renders to an empty string.
    /// </summary>
    /// <remarks>
    /// Never throws: a fault must not lose a turn already streamed, so it logs and returns the escaped input.
    /// </remarks>
    string Render(string? markdown);
}

/// <summary>
/// Renders markdown with Markdig, with raw HTML passthrough disabled and link schemes restricted.
/// </summary>
/// <remarks>
/// A trust boundary: model output is shaped by documents and MCP results, and prompts render through
/// the same <see cref="MarkdownPipelines.Default"/> the export renderers share.
/// </remarks>
public sealed partial class MarkdownRenderer(ILogger<MarkdownRenderer> logger) : IMarkdownRenderer
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
            LogHtmlRenderFailed(_logger, ex, markdown.Length);

            return WebUtility.HtmlEncode(markdown);
        }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "Rendering a {Length}-character message to HTML failed; storing escaped plain text instead.")]
    private static partial void LogHtmlRenderFailed(ILogger logger, Exception exception, int length);

    #endregion
}
