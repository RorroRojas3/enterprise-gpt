using System.Net;
using System.Text;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Writes the renderer-neutral block model as HTML.
/// </summary>
/// <remarks>
/// Every span of text is encoded here. The blocks carry model output, and the pipeline that built
/// them refuses raw HTML precisely so that nothing downstream puts markup back.
/// </remarks>
public static class HtmlBlockWriter
{
    /// <summary>
    /// Writes blocks as HTML.
    /// </summary>
    /// <param name="blocks">The blocks, in document order.</param>
    /// <returns>The markup, carrying no wrapping element of its own.</returns>
    public static string Write(IReadOnlyList<ExportBlock> blocks)
    {
        var builder = new StringBuilder();
        WriteBlocks(builder, blocks);

        return builder.ToString();
    }

    private static void WriteBlocks(StringBuilder builder, IReadOnlyList<ExportBlock> blocks)
    {
        foreach (var block in blocks)
        {
            WriteBlock(builder, block);
        }
    }

    private static void WriteBlock(StringBuilder builder, ExportBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var level = Math.Clamp(heading.Level, 1, 6);
                builder.Append("<h").Append(level).Append('>');
                WriteRuns(builder, heading.Runs);
                builder.Append("</h").Append(level).Append('>');
                break;

            case ParagraphBlock paragraph:
                builder.Append("<p>");
                WriteRuns(builder, paragraph.Runs);
                builder.Append("</p>");
                break;

            case CodeBlock code:
                WriteCode(builder, code);
                break;

            case QuoteBlock quote:
                builder.Append("<blockquote>");
                WriteBlocks(builder, quote.Children);
                builder.Append("</blockquote>");
                break;

            case ListBlock list:
                WriteList(builder, list);
                break;

            case TableBlock table:
                WriteTable(builder, table);
                break;

            case ThematicBreakBlock:
                builder.Append("<hr />");
                break;

            default:
                break;
        }
    }

    // The language line mirrors the head the app draws above a code block, and is what the Word and
    // PDF renderers emit in their own idiom from the same CodeBlock.Language.
    private static void WriteCode(StringBuilder builder, CodeBlock code)
    {
        builder.Append("<div class=\"code\">");

        if (!string.IsNullOrWhiteSpace(code.Language))
        {
            builder.Append("<div class=\"code__lang\">").Append(Encode(code.Language)).Append("</div>");
        }

        // Normalized, like the Word and PDF renderers do: a code block's line breaks are content,
        // and which pair of characters carries them should not depend on the host that rendered.
        builder
            .Append("<pre><code>")
            .Append(Encode(code.Text.ReplaceLineEndings("\n")))
            .Append("</code></pre></div>");
    }

    private static void WriteList(StringBuilder builder, ListBlock list)
    {
        var tag = list.Ordered ? "ol" : "ul";

        builder.Append('<').Append(tag).Append('>');

        foreach (var item in list.Items)
        {
            builder.Append("<li>");

            // The item's own first paragraph is written inline, so a one-line item is one line; a
            // second paragraph, a nested list or a fence inside the same item stays a block. The Word
            // and PDF renderers make the same split, for the same reason.
            var first = true;

            foreach (var child in item.Children)
            {
                if (first && child is ParagraphBlock paragraph)
                {
                    WriteRuns(builder, paragraph.Runs);
                    first = false;

                    continue;
                }

                WriteBlock(builder, child);
                first = false;
            }

            builder.Append("</li>");
        }

        builder.Append("</").Append(tag).Append('>');
    }

    private static void WriteTable(StringBuilder builder, TableBlock table)
    {
        IReadOnlyList<TableRowBlock> rows =
            table.HeaderRow is null ? table.Rows : [table.HeaderRow, .. table.Rows];

        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(row => row.Cells.Count);

        if (columns == 0)
        {
            return;
        }

        builder.Append("<table>");

        if (table.HeaderRow is not null)
        {
            builder.Append("<thead>");
            WriteRow(builder, table.HeaderRow, columns, header: true);
            builder.Append("</thead>");
        }

        if (table.Rows.Count > 0)
        {
            builder.Append("<tbody>");

            foreach (var row in table.Rows)
            {
                WriteRow(builder, row, columns, header: false);
            }

            builder.Append("</tbody>");
        }

        builder.Append("</table>");
    }

    private static void WriteRow(StringBuilder builder, TableRowBlock row, int columns, bool header)
    {
        builder.Append("<tr>");

        for (var column = 0; column < columns; column++)
        {
            // Ragged rows are padded rather than truncated: a short row emitting fewer cells would
            // pull every later column of the table one place left.
            var cell = column < row.Cells.Count ? row.Cells[column] : [];

            builder.Append(header ? "<th>" : "<td>");
            WriteRuns(builder, cell);
            builder.Append(header ? "</th>" : "</td>");
        }

        builder.Append("</tr>");
    }

    private static void WriteRuns(StringBuilder builder, IReadOnlyList<ExportRun> runs)
    {
        foreach (var run in runs)
        {
            WriteRun(builder, run);
        }
    }

    private static void WriteRun(StringBuilder builder, ExportRun run)
    {
        var linked = !string.IsNullOrEmpty(run.LinkUrl);

        if (linked)
        {
            builder
                .Append("<a href=\"")
                .Append(Encode(run.LinkUrl!))
                .Append("\" rel=\"noopener noreferrer\">");
        }

        var bold = run.Styles.HasFlag(ExportRunStyles.Bold);
        var italic = run.Styles.HasFlag(ExportRunStyles.Italic);
        var code = run.Styles.HasFlag(ExportRunStyles.Code);

        if (bold)
        {
            builder.Append("<strong>");
        }

        if (italic)
        {
            builder.Append("<em>");
        }

        if (code)
        {
            builder.Append("<code>");
        }

        // A hard break inside a run is a line break in the source; the element is what carries it,
        // since a raw newline collapses to a space.
        var segments = run.Text.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < segments.Length; index++)
        {
            if (index > 0)
            {
                builder.Append("<br />");
            }

            builder.Append(Encode(segments[index]));
        }

        if (code)
        {
            builder.Append("</code>");
        }

        if (italic)
        {
            builder.Append("</em>");
        }

        if (bold)
        {
            builder.Append("</strong>");
        }

        if (linked)
        {
            builder.Append("</a>");
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
