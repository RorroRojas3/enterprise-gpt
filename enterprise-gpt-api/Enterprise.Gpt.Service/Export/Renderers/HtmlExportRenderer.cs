using System.Globalization;
using System.Net;
using System.Text;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Assembles the HTML export from the HTML already stored beside each message.
/// </summary>
/// <remarks>
/// The one renderer that <em>reads</em> rather than re-renders: storing <c>htmlContent</c> at persist
/// time exists so this export does not depend on a client, or on this service, rendering markdown
/// again. The three formats US-1501 added cannot do the same — Word and PDF need structure, not
/// markup — which is why they go through <see cref="IMarkdownBlockMapper"/> instead.
/// </remarks>
public sealed class HtmlExportRenderer : IConversationExportRenderer
{
    private const string MessagesPlaceholder = "{{MESSAGES}}";
    private const string DatePlaceholder = "{{DATE}}";
    private const string TitlePlaceholder = "{{TITLE}}";

    /// <summary>
    /// The export template, read once on first use.
    /// </summary>
    /// <remarks>
    /// Shipped as content beside the assembly rather than embedded, matching how the prompt templates
    /// in this project are loaded. <see cref="Lazy{T}"/> rather than a plain static initializer so
    /// that reading <see cref="TemplatePath"/> — which composition does, to decide whether to register
    /// this renderer at all — cannot run the load and throw. A static initializer would only *happen*
    /// not to run there, by the CLR's <c>beforefieldinit</c> latitude, which is a permission rather
    /// than a promise.
    /// </remarks>
    private static readonly Lazy<string> Template = new(LoadTemplate);

    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Html;

    /// <inheritdoc />
    public ConversationExport Render(ConversationExportDocument document)
    {
        var body = new StringBuilder();

        foreach (var message in document.Messages)
        {
            var role = message.Role is Common.Enums.ChatRoles.User ? "user" : "assistant";

            // Stored HTML when there is any, and escaped plain text when there is not — a message
            // written before server-side rendering existed still belongs in the export.
            var content = string.IsNullOrEmpty(message.Html)
                ? $"<p>{WebUtility.HtmlEncode(message.Markdown)}</p>"
                : message.Html;

            body.Append(CultureInfo.InvariantCulture,
                $"""
                <div class="message {role}">
                    <div class="message-header">{WebUtility.HtmlEncode(message.Label)}</div>
                    <div class="message-content">{content}</div>
                </div>

                """);
        }

        // The messages go in last, and that ordering is load-bearing. Splicing them first leaves the
        // title and date substitutions running over the transcript itself, so an answer that happens
        // to contain the placeholder text has it silently rewritten to the conversation's name — and
        // message text is exactly the part of this document a model chooses.
        //
        // The name is escaped: it is user-supplied, and the title is the one place it reaches markup.
        var html = Template.Value
            .Replace(TitlePlaceholder, WebUtility.HtmlEncode(document.Name), StringComparison.Ordinal)
            .Replace(DatePlaceholder, document.ExportedAt.ToString("u", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace(MessagesPlaceholder, body.ToString(), StringComparison.Ordinal);

        return new ConversationExport(
            Encoding.UTF8.GetBytes(html),
            "text/html; charset=utf-8",
            $"{ExportFileNames.Stem(document.Name)}.html");
    }

    /// <summary>
    /// Gets the path the template is loaded from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exposed so composition can decline to register this renderer when the file is missing. The
    /// alternative is worse than it looks: the template loads from a static initializer, and the
    /// service resolves <see cref="IConversationExportRenderer"/> as a collection, so a missing file
    /// would surface as a <see cref="TypeInitializationException"/> on the first export of <em>any</em>
    /// format — including the three that never touch it — and no handler arm maps that to anything but
    /// a bare 500.
    /// </para>
    /// <para>
    /// Computed per read rather than cached in a static field: static field initializers run in
    /// declaration order, and an earlier cut that cached this had <see cref="LoadTemplate"/> reading
    /// it while it was still empty — six tests, and a <c>FileNotFoundException</c> naming the path
    /// <c>''</c>.
    /// </para>
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
}
