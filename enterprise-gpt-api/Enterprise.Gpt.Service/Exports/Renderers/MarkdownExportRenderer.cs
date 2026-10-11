using System.Globalization;
using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Enterprise.Gpt.Service.Exports.Models;

namespace Enterprise.Gpt.Service.Exports.Renderers;

/// <summary>
/// Writes a conversation as CommonMark.
/// </summary>
/// <remarks>
/// Messages are emitted verbatim: a Markdig round trip would normalize whitespace, renumber lists and
/// rewrite fences.
/// </remarks>
public sealed class MarkdownExportRenderer : IConversationExportRenderer
{
    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Markdown;

    /// <inheritdoc />
    public ConversationExport Render(ConversationExportDocument document)
    {
        var builder = new StringBuilder();

        // Not escaped: the worst a stray '#' or '*' in a single-line name can do is render as emphasis.
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

    #region Private methods

    // A newline inside the name would end the heading and turn the rest into body text.
    private static string SingleLine(string value) =>
        value.ReplaceLineEndings(" ").Trim();

    #endregion
}
