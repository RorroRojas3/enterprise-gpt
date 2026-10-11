using System.Globalization;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports.Fonts.Constants;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Enterprise.Gpt.Service.Exports.Models;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace Enterprise.Gpt.Service.Exports.Renderers;

/// <summary>
/// Writes a conversation as a PDF.
/// </summary>
/// <remarks>
/// Shares the block model and <see cref="ExportTheme"/> with the Word and HTML renderers. Embedded glyphs
/// mean it alone can fail for want of a font, so it is left unregistered and the route answers <c>503</c>.
/// </remarks>
public sealed class PdfExportRenderer(IMarkdownBlockMapper mapper) : IConversationExportRenderer
{
    private const string BodyStyle = "ExportBody";
    private const string TitleStyle = "ExportTitle";
    private const string SubtitleStyle = "ExportSubtitle";
    private const string LabelStyle = "ExportLabel";
    private const string CodeStyle = "ExportCode";
    private const string CodeLanguageStyle = "ExportCodeLanguage";
    private const string CellStyle = "ExportCell";

    /// <summary>Points per nesting level.</summary>
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
            var style = message.Role is ChatRoles.User ? ExportBlockStyles.Prompt : ExportBlockStyles.None;

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

    #region Private methods

    private void AppendBlock(Section section, ExportBlock block, int indentLevel, ExportBlockStyles style)
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
        Section section, IReadOnlyList<ExportRun> runs, string styleName, int indentLevel, ExportBlockStyles style)
    {
        var paragraph = section.AddParagraph();
        paragraph.Style = styleName;
        paragraph.Format.LeftIndent = Unit.FromPoint(indentLevel * IndentStepPoints);
        Enclose(paragraph, style);
        AppendRuns(paragraph, runs);
    }

    // One paragraph per line, since MigraDoc's wrapping would reflow the breaks; the shading is per
    // paragraph, so a short line still reads as part of the block.
    private static void AppendCode(Section section, CodeBlock code, int indentLevel, ExportBlockStyles style)
    {
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

            // A blank line still needs a paragraph, holding a non-breaking space because MigraDoc
            // collapses an empty shaded paragraph to zero height.
            paragraph.AddText(line.Length > 0 ? line.Replace("\t", "    ", StringComparison.Ordinal) : " ");
        }
    }

    private void AppendQuote(Section section, QuoteBlock quote, int indentLevel, ExportBlockStyles style)
    {
        foreach (var child in quote.Children)
        {
            AppendBlock(section, child, indentLevel + 1, style | ExportBlockStyles.Quote);
        }
    }

    private void AppendList(Section section, ListBlock list, int indentLevel, ExportBlockStyles style)
    {
        var level = Math.Min(indentLevel, MaxListDepth);
        var listType = list.Ordered
            ? (ListType)((int)ListType.NumberList1 + level)
            : (ListType)((int)ListType.BulletList1 + level);

        var first = true;

        foreach (var item in list.Items)
        {
            // The marker is drawn even when the item opens with a list or fence, as an HTML <li> would.
            // Content after the first paragraph is indented, not numbered, or it would restart the count.
            var opening = item.Children is [ParagraphBlock lead, ..] ? lead : null;

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
        ExportBlockStyles style)
    {
        var paragraph = section.AddParagraph();
        paragraph.Style = BodyStyle;
        paragraph.Format.LeftIndent = Unit.FromPoint((level + 1) * IndentStepPoints);
        paragraph.Format.SpaceAfter = Unit.FromPoint(2);
        paragraph.Format.ListInfo = new ListInfo
        {
            ListType = listType,
            // False restarts an ordered list at 1; true on later items stops each being numbered "1".
            ContinuePreviousList = continues
        };

        Enclose(paragraph, style);
        AppendRuns(paragraph, runs);
    }

    private static void AppendTable(Section section, TableBlock table, int indentLevel, ExportBlockStyles style)
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

        // Split evenly: content-derived widths need a measuring pass MigraDoc does not expose.
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

            // Filled row by row: MigraDoc has no ancestor for a prompt's band to come from.
            if (isHeader)
            {
                row.Shading.Color = Rgb(ExportTheme.Colors.SurfaceAlt);
            }
            else if (style.HasFlag(ExportBlockStyles.Prompt))
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

        // MigraDoc gives a table no spacing; without this the next paragraph sits on its border.
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
    /// Draws a prompt's band or a quote's rule. A <paramref name="keepsOwnShading"/> paragraph (code) gets
    /// the rule alone, so prose and code stay distinct.
    /// </summary>
    private static Paragraph Enclose(
        Paragraph paragraph, ExportBlockStyles style, bool keepsOwnShading = false)
    {
        var prompt = style.HasFlag(ExportBlockStyles.Prompt);

        // One rule per paragraph is all MigraDoc's left border offers, so a quote inside a prompt
        // keeps the prompt's: the outer container is the one a reader loses more by not seeing.
        if (prompt || style.HasFlag(ExportBlockStyles.Quote))
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
            // MigraDoc needs a line-break element rather than the character.
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
        // A MigraDoc Hyperlink is a container, so the styling goes on the text inside it.
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

    #endregion
}
