using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Writes a conversation as a self-contained HTML document.
/// </summary>
/// <remarks>
/// The design base of the three rendered formats: every value in its template comes from
/// <see cref="ExportTheme"/>, which the Word and PDF renderers read too, and its content comes from
/// the same <see cref="IMarkdownBlockMapper"/> walk they consume.
/// </remarks>
/// <param name="mapper">Maps each message's markdown into blocks.</param>
public sealed partial class HtmlExportRenderer(IMarkdownBlockMapper mapper) : IConversationExportRenderer
{
    /// <summary>
    /// The export template, read once on first use.
    /// </summary>
    /// <remarks>
    /// Shipped as content beside the assembly rather than embedded, matching how this project's
    /// prompt templates load. <see cref="Lazy{T}"/> rather than a static initializer, so that
    /// reading <see cref="TemplatePath"/> — which composition does, to decide whether to register
    /// this renderer at all — cannot run the load and throw.
    /// </remarks>
    private static readonly Lazy<string> Template = new(LoadTemplate);

    private static readonly Lazy<string> Theme = new(BuildTheme);

    private readonly IMarkdownBlockMapper _mapper = mapper;

    [GeneratedRegex(@"\{\{(THEME|TITLE|DATE|MESSAGES)\}\}")]
    private static partial Regex Placeholder();

    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Html;

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

        // One pass, so nothing spliced in is scanned again. Chained Replace calls are not
        // equivalent: HTML-encoding leaves "{{MESSAGES}}" untouched — it holds no encodable
        // character — so a conversation named that would have the whole transcript spliced into its
        // own <title>, and the name is text a user chooses.
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

    /// <summary>
    /// Gets the path the template is loaded from.
    /// </summary>
    /// <remarks>
    /// Exposed so composition can decline to register this renderer when the file is missing: the
    /// renderers resolve as a collection, so an absent template would otherwise fault the first
    /// export of every format rather than of HTML. Computed per read rather than cached, because a
    /// static field initialising after <see cref="Template"/> would be read while still empty.
    /// </remarks>
    public static string TemplatePath =>
        Path.Combine(AppContext.BaseDirectory, "Files", "conversation-history.html");

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
}
