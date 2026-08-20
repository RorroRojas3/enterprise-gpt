using System.Globalization;
using Enterprise.Gpt.Service.Export.Fonts;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using MigraTable = MigraDoc.DocumentObjectModel.Tables.Table;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Writes a conversation as a PDF.
/// </summary>
/// <remarks>
/// <para>
/// The same block model the Word renderer consumes, so the two cannot disagree about what a message's
/// markdown meant. What differs is everything below that: a PDF is laid out here and now, with the
/// glyphs embedded, where a .docx defers both to whatever opens it.
/// </para>
/// <para>
/// Because the glyphs are embedded, this renderer — alone among the five — can fail for a reason that
/// has nothing to do with the conversation: no font. That is handled by not registering it, so the
/// route answers <c>503</c> up front rather than throwing partway through a response body.
/// </para>
/// </remarks>
/// <param name="mapper">Maps each message's markdown into blocks.</param>
public sealed class PdfExportRenderer(IMarkdownBlockMapper mapper) : IConversationExportRenderer
{
    private const string BodyStyle = "ExportBody";
    private const string TitleStyle = "ExportTitle";
    private const string SubtitleStyle = "ExportSubtitle";
    private const string LabelStyle = "ExportLabel";
    private const string CodeStyle = "ExportCode";
    private const string CellStyle = "ExportCell";
    private const string LinkColour = "0B5FA5";
    private const string MutedColour = "5A6E82";
    private const string RuleColour = "D0D7DE";
    private const string CodeFill = "F3F5F7";

    /// <summary>How far one nesting level indents. MigraDoc measures in points.</summary>
    private const int IndentStepPoints = 18;

    /// <summary>MigraDoc's list levels, matching what its own bullet styles offer.</summary>
    private const int MaxListDepth = 2;

    private readonly IMarkdownBlockMapper _mapper = mapper;

    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Pdf;

    /// <inheritdoc />
    public ConversationExport Render(ConversationExportDocument document)
    {
        var pdf = new Document { Info = { Title = document.Name } };
        DefineStyles(pdf);

        var section = pdf.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        AddText(section.AddParagraph(), document.Name, TitleStyle);
        AddText(
            section.AddParagraph(),
            $"Exported {document.ExportedAt.ToString("u", CultureInfo.InvariantCulture)}",
            SubtitleStyle);

        foreach (var message in document.Messages)
        {
            AddText(section.AddParagraph(), message.Label, LabelStyle);

            foreach (var block in _mapper.Map(message.Markdown))
            {
                AppendBlock(section, block, indentLevel: 0);
            }
        }

        var renderer = new PdfDocumentRenderer { Document = pdf };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.Save(stream, closeStream: false);

        return new ConversationExport(
            stream.ToArray(),
            "application/pdf",
            $"{ExportFileNames.Stem(document.Name)}.pdf");
    }

    private void AppendBlock(Section section, ExportBlock block, int indentLevel)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AppendParagraph(section, heading.Runs, $"Heading{heading.Level}", indentLevel);
                break;

            case ParagraphBlock paragraph:
                AppendParagraph(section, paragraph.Runs, BodyStyle, indentLevel);
                break;

            case CodeBlock code:
                AppendCode(section, code, indentLevel);
                break;

            case QuoteBlock quote:
                AppendQuote(section, quote, indentLevel);
                break;

            case ListBlock list:
                AppendList(section, list, indentLevel);
                break;

            case TableBlock table:
                AppendTable(section, table);
                break;

            case ThematicBreakBlock:
                AppendRule(section);
                break;

            default:
                break;
        }
    }

    private static void AppendParagraph(
        Section section, IReadOnlyList<ExportRun> runs, string style, int indentLevel)
    {
        var paragraph = section.AddParagraph();
        paragraph.Style = style;
        paragraph.Format.LeftIndent = Unit.FromPoint(indentLevel * IndentStepPoints);
        AppendRuns(paragraph, runs);
    }

    // One MigraDoc paragraph per line: a code block's line breaks are content, and MigraDoc's own
    // wrapping would reflow them. The shading is per paragraph, so a short line still reads as part
    // of the block.
    private static void AppendCode(Section section, CodeBlock code, int indentLevel)
    {
        var lines = code.Text.ReplaceLineEndings("\n").Split('\n');

        foreach (var line in lines)
        {
            var paragraph = section.AddParagraph();
            paragraph.Style = CodeStyle;
            paragraph.Format.LeftIndent = Unit.FromPoint(indentLevel * IndentStepPoints);

            // A blank line still needs a paragraph, or the block loses its internal spacing. A
            // non-breaking space rather than nothing, because MigraDoc collapses an empty shaded
            // paragraph to zero height.
            paragraph.AddText(line.Length > 0 ? line.Replace("\t", "    ", StringComparison.Ordinal) : " ");
        }
    }

    private void AppendQuote(Section section, QuoteBlock quote, int indentLevel)
    {
        foreach (var child in quote.Children)
        {
            AppendBlock(section, child, indentLevel + 1);
        }
    }

    private void AppendList(Section section, ListBlock list, int indentLevel)
    {
        var level = Math.Min(indentLevel, MaxListDepth);
        var listType = list.Ordered
            ? (ListType)((int)ListType.NumberList1 + level)
            : (ListType)((int)ListType.BulletList1 + level);

        var first = true;

        foreach (var item in list.Items)
        {
            var firstBlockOfItem = true;

            foreach (var child in item.Children)
            {
                // Only the item's own first paragraph carries the bullet or the number. A nested
                // list, a second paragraph or a code block inside the same item is indented instead —
                // numbering a continuation line restarts the count against it.
                if (firstBlockOfItem && child is ParagraphBlock paragraph)
                {
                    AppendListItem(section, paragraph.Runs, listType, level, continues: !first);
                    firstBlockOfItem = false;
                    first = false;

                    continue;
                }

                AppendBlock(section, child, indentLevel + 1);
                firstBlockOfItem = false;
            }

            if (item.Children.Count == 0)
            {
                AppendListItem(section, [], listType, level, continues: !first);
                first = false;
            }
        }
    }

    private static void AppendListItem(
        Section section, IReadOnlyList<ExportRun> runs, ListType listType, int level, bool continues)
    {
        var paragraph = section.AddParagraph();
        paragraph.Style = BodyStyle;
        paragraph.Format.LeftIndent = Unit.FromPoint((level + 1) * IndentStepPoints);
        paragraph.Format.SpaceAfter = Unit.FromPoint(2);
        paragraph.Format.ListInfo = new ListInfo
        {
            ListType = listType,
            // False on the first item restarts an ordered list at 1; true on the rest is what stops
            // every item being numbered "1".
            ContinuePreviousList = continues
        };

        AppendRuns(paragraph, runs);
    }

    private static void AppendTable(Section section, TableBlock table)
    {
        var rows = table.HeaderRow is null ? table.Rows : [table.HeaderRow, .. table.Rows];

        if (rows.Count == 0)
        {
            return;
        }

        var columnCount = rows.Max(row => row.Cells.Count);

        if (columnCount == 0)
        {
            return;
        }

        var migraTable = section.AddTable();
        migraTable.Borders.Width = 0.5;
        migraTable.Borders.Color = new Color(Convert.ToUInt32(RuleColour, 16) | 0xFF000000);
        migraTable.LeftPadding = Unit.FromPoint(4);
        migraTable.RightPadding = Unit.FromPoint(4);
        migraTable.TopPadding = Unit.FromPoint(2);
        migraTable.BottomPadding = Unit.FromPoint(2);

        // The text column split evenly. A width derived from content would need a measuring pass
        // MigraDoc does not expose before rendering.
        var usable = section.PageSetup.PageWidth.Point
            - section.PageSetup.LeftMargin.Point
            - section.PageSetup.RightMargin.Point;

        for (var column = 0; column < columnCount; column++)
        {
            migraTable.AddColumn(Unit.FromPoint(usable / columnCount));
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var isHeader = table.HeaderRow is not null && index == 0;
            var row = migraTable.AddRow();
            row.HeadingFormat = isHeader;

            for (var column = 0; column < columnCount; column++)
            {
                var cellRuns = column < rows[index].Cells.Count ? rows[index].Cells[column] : [];
                var paragraph = row.Cells[column].AddParagraph();
                paragraph.Style = CellStyle;

                AppendRuns(
                    paragraph,
                    isHeader
                        ? [.. cellRuns.Select(run => run with { Styles = run.Styles | ExportRunStyles.Bold })]
                        : cellRuns);
            }
        }

        // MigraDoc gives a table no space of its own; without this the next paragraph sits on its
        // bottom border.
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
    }

    private static void AppendRule(Section section)
    {
        var paragraph = section.AddParagraph();
        paragraph.Format.SpaceBefore = Unit.FromPoint(6);
        paragraph.Format.SpaceAfter = Unit.FromPoint(6);
        paragraph.Format.Borders.Bottom.Width = 0.75;
        paragraph.Format.Borders.Bottom.Color = new Color(Convert.ToUInt32(RuleColour, 16) | 0xFF000000);
    }

    private static void AppendRuns(Paragraph paragraph, IReadOnlyList<ExportRun> runs)
    {
        foreach (var run in runs)
        {
            // A hard break inside a run is a line break in the source; MigraDoc needs the element
            // rather than the character.
            var segments = run.Text.ReplaceLineEndings("\n").Split('\n');

            for (var index = 0; index < segments.Length; index++)
            {
                if (index > 0)
                {
                    paragraph.AddLineBreak();
                }

                if (segments[index].Length == 0)
                {
                    continue;
                }

                AppendSegment(paragraph, segments[index], run);
            }
        }
    }

    private static void AppendSegment(Paragraph paragraph, string text, ExportRun run)
    {
        // MigraDoc's Hyperlink is a container of formatted text, so the styling has to be applied
        // inside it rather than to it.
        if (run.LinkUrl is not null && Uri.TryCreate(run.LinkUrl, UriKind.Absolute, out var uri))
        {
            var link = paragraph.AddHyperlink(uri.AbsoluteUri, HyperlinkType.Web);
            var linked = link.AddFormattedText(text);
            ApplyStyles(linked.Font, run.Styles);
            linked.Font.Color = new Color(Convert.ToUInt32(LinkColour, 16) | 0xFF000000);
            linked.Font.Underline = Underline.Single;

            return;
        }

        var formatted = paragraph.AddFormattedText(text);
        ApplyStyles(formatted.Font, run.Styles);
    }

    private static void ApplyStyles(Font font, ExportRunStyles styles)
    {
        font.Bold = styles.HasFlag(ExportRunStyles.Bold);
        font.Italic = styles.HasFlag(ExportRunStyles.Italic);

        if (styles.HasFlag(ExportRunStyles.Code))
        {
            font.Name = ExportFontFamilies.Mono;
            font.Size = Unit.FromPoint(9);
        }
    }

    private static void AddText(Paragraph paragraph, string text, string style)
    {
        paragraph.Style = style;
        paragraph.AddText(text);
    }

    private static void DefineStyles(Document document)
    {
        // Every style descends from Normal, so naming the export face once here is what puts it on
        // the whole document — including MigraDoc's built-in heading styles.
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = ExportFontFamilies.Sans;
        normal.Font.Size = Unit.FromPoint(10.5);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var body = document.Styles.AddStyle(BodyStyle, StyleNames.Normal);
        body.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var title = document.Styles.AddStyle(TitleStyle, StyleNames.Normal);
        title.Font.Size = Unit.FromPoint(20);
        title.Font.Bold = true;
        title.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var subtitle = document.Styles.AddStyle(SubtitleStyle, StyleNames.Normal);
        subtitle.Font.Size = Unit.FromPoint(9);
        subtitle.Font.Color = new Color(Convert.ToUInt32(MutedColour, 16) | 0xFF000000);
        subtitle.ParagraphFormat.SpaceAfter = Unit.FromPoint(18);

        var label = document.Styles.AddStyle(LabelStyle, StyleNames.Normal);
        label.Font.Size = Unit.FromPoint(11);
        label.Font.Bold = true;
        label.Font.Color = new Color(0xFF14324F);
        label.ParagraphFormat.SpaceBefore = Unit.FromPoint(16);
        label.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        label.ParagraphFormat.KeepWithNext = true;

        var code = document.Styles.AddStyle(CodeStyle, StyleNames.Normal);
        code.Font.Name = ExportFontFamilies.Mono;
        code.Font.Size = Unit.FromPoint(9);
        code.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);
        code.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        code.ParagraphFormat.LineSpacing = 1.15;
        code.ParagraphFormat.Shading.Color = new Color(Convert.ToUInt32(CodeFill, 16) | 0xFF000000);

        var cell = document.Styles.AddStyle(CellStyle, StyleNames.Normal);
        cell.Font.Size = Unit.FromPoint(9.5);
        cell.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        for (var level = 1; level <= 6; level++)
        {
            var heading = document.Styles[$"Heading{level}"]!;
            heading.Font.Name = ExportFontFamilies.Sans;
            heading.Font.Bold = true;
            heading.Font.Size = Unit.FromPoint(Math.Max(11, 18 - (level * 1.5)));
            heading.Font.Color = new Color(0xFF14324F);
            heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
            heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
            heading.ParagraphFormat.KeepWithNext = true;
        }
    }
}
