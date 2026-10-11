using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Enterprise.Gpt.Service.Exports.Models;

namespace Enterprise.Gpt.Service.Exports.Renderers;

/// <summary>
/// Writes a conversation as a self-contained HTML document.
/// </summary>
/// <remarks>
/// The design base of the rendered formats: values come from <see cref="ExportTheme"/> and content from
/// <see cref="IMarkdownBlockMapper"/>, both shared with the Word and PDF renderers.
/// </remarks>
public sealed partial class HtmlExportRenderer(IMarkdownBlockMapper mapper) : IConversationExportRenderer
{
    /// <summary>
    /// Lazy, so composition can read <see cref="TemplatePath"/> without running the load and throwing.
    /// </summary>
    private static readonly Lazy<string> Template = new(LoadTemplate);

    private static readonly Lazy<string> Theme = new(BuildTheme);

    private readonly IMarkdownBlockMapper _mapper = mapper;

    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Html;

    /// <summary>
    /// Exposed so composition can skip this renderer when the file is missing, rather than fault every
    /// format's export. Computed per read, since a static field could be read before it initialized.
    /// </summary>
    public static string TemplatePath =>
        Path.Combine(AppContext.BaseDirectory, "Files", "conversation-history.html");

    /// <inheritdoc />
    public ConversationExport Render(ConversationExportDocument document)
    {
        var body = new StringBuilder();

        foreach (var message in document.Messages)
        {
            var role = message.Role is ChatRoles.User ? "user" : "assistant";

            body.Append(CultureInfo.InvariantCulture,
                $"""
                <article class="message message--{role}">
                    <p class="message__role">{WebUtility.HtmlEncode(message.Label)}</p>
                    <div class="message__body">{HtmlBlockWriter.Write(_mapper.Map(message.Markdown))}</div>
                </article>

                """);
        }

        // One pass, so spliced text is never rescanned: a conversation named "{{MESSAGES}}" survives HTML
        // encoding and would otherwise pull the whole transcript into its <title>.
        var html = Placeholder().Replace(Template.Value, match => match.Groups[1].Value switch
        {
            "THEME" => Theme.Value,
            "TITLE" => WebUtility.HtmlEncode(document.Name),
            "DATE" => document.ExportedAt.ToString("u", CultureInfo.InvariantCulture),
            _ => body.ToString()
        });

        return new ConversationExport(
            Encoding.UTF8.GetBytes(html),
            "text/html; charset=utf-8",
            $"{ExportFileNames.Stem(document.Name)}.html");
    }

    #region Private methods

    [GeneratedRegex(@"\{\{(THEME|TITLE|DATE|MESSAGES)\}\}")]
    private static partial Regex Placeholder();

    private static string LoadTemplate()
    {
        return File.Exists(TemplatePath)
            ? File.ReadAllText(TemplatePath)
            : throw new FileNotFoundException(
                $"The conversation export template was not found at '{TemplatePath}'.", TemplatePath);
    }

    private static string BuildTheme()
    {
        var builder = new StringBuilder();
        builder.AppendLine("      :root {");

        foreach (var (name, value) in ExportTheme.CssVariables)
        {
            builder.Append("        ").Append(name).Append(": ").Append(value).AppendLine(";");
        }

        return builder.Append("      }").ToString();
    }

    #endregion
}
