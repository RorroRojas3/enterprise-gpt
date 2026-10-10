using System.Globalization;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Export.Fonts;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Writes a conversation as a PDF.
/// </summary>
/// <remarks>
/// <para>
/// The same block model the Word renderer consumes and the same <see cref="ExportTheme"/> the HTML
/// template resolves against, so the three cannot disagree about what a message's markdown meant or
/// what colour it is. What differs is everything below that: a PDF is laid out here and now, with the
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
    private const string CodeLanguageStyle = "ExportCodeLanguage";
    private const string CellStyle = "ExportCell";

    /// <summary>How far one nesting level indents. MigraDoc measures in points.</summary>
    private const int IndentStepPoints = 18;

    /// <summary>Points. The 2.25pt rule the app draws down the side of a prompt.</summary>
    private const double PromptRuleWidth = 2.25;

    /// <summary>Points, matching the 2px edge the HTML export draws on a block quote.</summary>
    private const double QuoteRuleWidth = 1.5;

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
            // A prompt is banded rather than bubbled — the same tint and left rule the HTML and Word
            // exports draw, because a right-aligned bubble does not survive page flow.
            var style = message.Role is ChatRoles.User ? ExportBlockStyle.Prompt : ExportBlockStyle.None;

            Enclose(AddText(section.AddParagraph(), message.Label, LabelStyle), style);

            foreach (var block in _mapper.Map(message.Markdown))
            {
                AppendBlock(section, block, indentLevel: 0, style);
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

    private void AppendBlock(Section section, ExportBlock block, int indentLevel, ExportBlockStyle style)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AppendParagraph(
                    section, heading.Runs, $"Heading{Math.Clamp(heading.Level, 1, 6)}", indentLevel, style);
                break;

            case ParagraphBlock paragraph:
                AppendParagraph(section, paragraph.Runs, BodyStyle, indentLevel, style);
                break;

            case CodeBlock code:
                AppendCode(section, code, indentLevel, style);
                break;

            case QuoteBlock quote:
                AppendQuote(section, quote, indentLevel, style);
                break;

            case ListBlock list:
                AppendList(section, list, indentLevel, style);
                break;

            case TableBlock table:
                AppendTable(section, table, indentLevel, style);
                break;

            case ThematicBreakBlock:
                AppendRule(section);
                break;

            default:
                break;
        }
    }

    private static void AppendParagraph(
        Section section, IReadOnlyList<ExportRun> runs, string styleName, int indentLevel, ExportBlockStyle style)
    {
        var paragraph = section.AddParagraph();
        paragraph.Style = styleName;
        paragraph.Format.LeftIndent = Unit.FromPoint(indentLevel * IndentStepPoints);
        Enclose(paragraph, style);
        AppendRuns(paragraph, runs);
    }

    // One MigraDoc paragraph per line: a code block's line breaks are content, and MigraDoc's own
    // wrapping would reflow them. The shading is per paragraph, so a short line still reads as part
    // of the block.
    private static void AppendCode(Section section, CodeBlock code, int indentLevel, ExportBlockStyle style)
    {
        // The fence's language, the way the app labels a code block and the HTML export repeats.
        if (!string.IsNullOrWhiteSpace(code.Language))
        {
            var label = section.AddParagraph();
            label.Style = CodeLanguageStyle;
            label.Format.LeftIndent = Unit.FromPoint(indentLevel * IndentStepPoints);
            Enclose(label, style, keepsOwnShading: true);
            label.AddText(code.Language);
        }

        var lines = code.Text.ReplaceLineEndings("\n").Split('\n');

        foreach (var line in lines)
        {
            var paragraph = section.AddParagraph();
            paragraph.Style = CodeStyle;
            paragraph.Format.LeftIndent = Unit.FromPoint(indentLevel * IndentStepPoints);
            Enclose(paragraph, style, keepsOwnShading: true);

            // A blank line still needs a paragraph, or the block loses its internal spacing. A
            // non-breaking space rather than nothing, because MigraDoc collapses an empty shaded
            // paragraph to zero height.
            paragraph.AddText(line.Length > 0 ? line.Replace("\t", "    ", StringComparison.Ordinal) : " ");
        }
    }

    private void AppendQuote(Section section, QuoteBlock quote, int indentLevel, ExportBlockStyle style)
    {
        foreach (var child in quote.Children)
        {
            AppendBlock(section, child, indentLevel + 1, style | ExportBlockStyle.Quote);
        }
    }

    private void AppendList(Section section, ListBlock list, int indentLevel, ExportBlockStyle style)
    {
        var level = Math.Min(indentLevel, MaxListDepth);
        var listType = list.Ordered
            ? (ListType)((int)ListType.NumberList1 + level)
            : (ListType)((int)ListType.BulletList1 + level);

        var first = true;

        foreach (var item in list.Items)
        {
            // The marker belongs to the item, so it is drawn whether or not the item opens with a
            // paragraph to hang it on — an item starting with a nested list or a fence would
            // otherwise lose its bullet, which an HTML <li> never does. Everything after the first
            // paragraph is indented rather than numbered: a continuation line carrying its own
            // number restarts the count against it.
            var opening = item.Children.FirstOrDefault() as ParagraphBlock;

            AppendListItem(section, opening?.Runs ?? [], listType, level, continues: !first, style);
            first = false;

            foreach (var child in item.Children.Skip(opening is null ? 0 : 1))
            {
                AppendBlock(section, child, indentLevel + 1, style);
            }
        }
    }

    private static void AppendListItem(
        Section section,
        IReadOnlyList<ExportRun> runs,
        ListType listType,
        int level,
        bool continues,
        ExportBlockStyle style)
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

        Enclose(paragraph, style);
        AppendRuns(paragraph, runs);
    }

    private static void AppendTable(Section section, TableBlock table, int indentLevel, ExportBlockStyle style)
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
        // The indent a nested table gets in place of the ancestor CSS gives the HTML export.
        migraTable.Rows.LeftIndent = Unit.FromPoint(indentLevel * IndentStepPoints);
        migraTable.Borders.Width = 0.5;
        migraTable.Borders.Color = Rgb(ExportTheme.Colors.Border);
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

            // A table inside a prompt is filled row by row, because MigraDoc has no ancestor for
            // the band to come from the way the HTML has.
            if (isHeader)
            {
                row.Shading.Color = Rgb(ExportTheme.Colors.SurfaceAlt);
            }
            else if (style.HasFlag(ExportBlockStyle.Prompt))
            {
                row.Shading.Color = Rgb(ExportTheme.Colors.UserBand);
            }

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
        paragraph.Format.Borders.Bottom.Color = Rgb(ExportTheme.Colors.Border);
    }

    /// <summary>
    /// Draws whatever encloses a paragraph — a prompt's band, a block quote's rule, or neither.
    /// </summary>
    /// <param name="keepsOwnShading">
    /// <see langword="true"/> for a paragraph whose style already fills it — a code block, which a
    /// band marks with its rule alone rather than erasing the distinction between prose and code.
    /// </param>
    private static Paragraph Enclose(
        Paragraph paragraph, ExportBlockStyle style, bool keepsOwnShading = false)
    {
        var prompt = style.HasFlag(ExportBlockStyle.Prompt);

        // One rule per paragraph is all MigraDoc's left border offers, so a quote inside a prompt
        // keeps the prompt's: the outer container is the one a reader loses more by not seeing.
        if (prompt || style.HasFlag(ExportBlockStyle.Quote))
        {
            paragraph.Format.Borders.Left.Width = prompt ? PromptRuleWidth : QuoteRuleWidth;
            paragraph.Format.Borders.Left.Color =
                Rgb(prompt ? ExportTheme.Colors.Brand : ExportTheme.Colors.Border);
            paragraph.Format.Borders.DistanceFromLeft = Unit.FromPoint(6);
        }

        if (prompt && !keepsOwnShading)
        {
            paragraph.Format.Shading.Color = Rgb(ExportTheme.Colors.UserBand);
        }

        return paragraph;
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
            linked.Font.Color = Rgb(ExportTheme.Colors.Link);
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
            font.Size = Unit.FromPoint(ExportTheme.Sizes.Code);
        }
    }

    private static Paragraph AddText(Paragraph paragraph, string text, string styleName)
    {
        paragraph.Style = styleName;
        paragraph.AddText(text);

        return paragraph;
    }

    // MigraDoc's Color takes packed ARGB, and the theme states colours the way markup does.
    private static Color Rgb(string hex) =>
        new(Convert.ToUInt32(hex, 16) | 0xFF000000);

    private static void DefineStyles(Document document)
    {
        // Every style descends from Normal, so naming the export face once here is what puts it on
        // the whole document — including MigraDoc's built-in heading styles.
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = ExportFontFamilies.Sans;
        normal.Font.Size = Unit.FromPoint(ExportTheme.Sizes.Body);
        normal.Font.Color = Rgb(ExportTheme.Colors.Text);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var body = document.Styles.AddStyle(BodyStyle, StyleNames.Normal);
        body.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var title = document.Styles.AddStyle(TitleStyle, StyleNames.Normal);
        title.Font.Size = Unit.FromPoint(ExportTheme.Sizes.Title);
        title.Font.Bold = true;
        title.Font.Color = Rgb(ExportTheme.Colors.Brand);
        title.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var subtitle = document.Styles.AddStyle(SubtitleStyle, StyleNames.Normal);
        subtitle.Font.Size = Unit.FromPoint(ExportTheme.Sizes.Subtitle);
        subtitle.Font.Color = Rgb(ExportTheme.Colors.Muted);
        subtitle.ParagraphFormat.SpaceAfter = Unit.FromPoint(18);

        var label = document.Styles.AddStyle(LabelStyle, StyleNames.Normal);
        label.Font.Size = Unit.FromPoint(ExportTheme.Sizes.RoleLabel);
        label.Font.Bold = true;
        label.Font.Color = Rgb(ExportTheme.Colors.Brand);
        label.ParagraphFormat.SpaceBefore = Unit.FromPoint(16);
        label.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        label.ParagraphFormat.KeepWithNext = true;

        var codeLanguage = document.Styles.AddStyle(CodeLanguageStyle, StyleNames.Normal);
        codeLanguage.Font.Name = ExportFontFamilies.Mono;
        codeLanguage.Font.Size = Unit.FromPoint(ExportTheme.Sizes.CodeLanguage);
        codeLanguage.Font.Color = Rgb(ExportTheme.Colors.Muted);
        codeLanguage.ParagraphFormat.SpaceBefore = Unit.FromPoint(6);
        codeLanguage.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);
        codeLanguage.ParagraphFormat.KeepWithNext = true;
        codeLanguage.ParagraphFormat.Shading.Color = Rgb(ExportTheme.Colors.SurfaceAlt);

        var code = document.Styles.AddStyle(CodeStyle, StyleNames.Normal);
        code.Font.Name = ExportFontFamilies.Mono;
        code.Font.Size = Unit.FromPoint(ExportTheme.Sizes.Code);
        code.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);
        code.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        code.ParagraphFormat.LineSpacing = 1.15;
        code.ParagraphFormat.Shading.Color = Rgb(ExportTheme.Colors.SurfaceAlt);

        var cell = document.Styles.AddStyle(CellStyle, StyleNames.Normal);
        cell.Font.Size = Unit.FromPoint(ExportTheme.Sizes.TableCell);
        cell.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        for (var level = 1; level <= 6; level++)
        {
            var heading = document.Styles[$"Heading{level}"]!;
            heading.Font.Name = ExportFontFamilies.Sans;
            heading.Font.Bold = true;
            heading.Font.Size = Unit.FromPoint(ExportTheme.Sizes.Heading(level));
            heading.Font.Color = Rgb(ExportTheme.Colors.Brand);
            heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
            heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
            heading.ParagraphFormat.KeepWithNext = true;
        }
    }
}
