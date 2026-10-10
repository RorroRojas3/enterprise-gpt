using System.Globalization;
using System.Text;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Writes a conversation as CommonMark.
/// </summary>
/// <remarks>
/// The one format that needs no mapping at all: each message's text <em>is</em> markdown, so it is
/// emitted verbatim. Nothing is escaped and nothing is re-wrapped — a round trip through Markdig
/// would normalize whitespace, renumber lists and rewrite fences, which is a worse export than the
/// text the model actually produced.
/// </remarks>
public sealed class MarkdownExportRenderer : IConversationExportRenderer
{
    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Markdown;

    /// <inheritdoc />
    public ConversationExport Render(ConversationExportDocument document)
    {
        var builder = new StringBuilder();

        // The name is written as an ATX heading rather than escaped: a conversation name is a single
        // line of plain text, and the worst a stray '#' or '*' in it can do is render as emphasis.
        builder.Append("# ").AppendLine(SingleLine(document.Name));
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"_Exported {document.ExportedAt.ToString("u", CultureInfo.InvariantCulture)}_");
        builder.AppendLine();

        foreach (var message in document.Messages)
        {
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine();
            builder.Append("## ").AppendLine(message.Label);
            builder.AppendLine();
            builder.AppendLine(message.Markdown.TrimEnd());
        }

        return new ConversationExport(
            Encoding.UTF8.GetBytes(builder.ToString()),
            "text/markdown; charset=utf-8",
            $"{ExportFileNames.Stem(document.Name)}.md");
    }

    // A newline inside the name would end the heading and turn the rest into body text.
    private static string SingleLine(string value) =>
        value.ReplaceLineEndings(" ").Trim();
}
