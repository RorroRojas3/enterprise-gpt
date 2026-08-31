using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Enterprise.Gpt.Common.Enums;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Writes a conversation as an Office Open XML word-processing document.
/// </summary>
/// <remarks>
/// <para>
/// Built from the block model rather than from the stored HTML: Word has no HTML importer this code
/// could hand markup to, and the alternative — an <c>altChunk</c> carrying raw HTML — makes the
/// document's contents depend on whichever version of Word opens it, and on that Word being willing
/// to run an import at all.
/// </para>
/// <para>
/// Colours and sizes come from <see cref="ExportTheme"/>, which the HTML and PDF renderers read too.
/// The faces do not: a .docx carries no glyphs, so this names what ships with Office rather than the
/// head of the theme's stack, and that is the one place the formats deliberately differ.
/// </para>
/// </remarks>
/// <param name="mapper">Maps each message's markdown into blocks.</param>
public sealed class WordExportRenderer(IMarkdownBlockMapper mapper) : IConversationExportRenderer
{
    private const int BulletNumberingId = 1;
    private const int OrderedNumberingId = 2;
    private const int MaxListDepth = 8;

    /// <summary>Twips of indent per list or quote level. 360 is Word's own half-inch-per-two-levels.</summary>
    private const int IndentStepTwips = 360;

    /// <summary>Eighths of a point. 18 is the 2.25pt rule the app draws down the side of a prompt.</summary>
    private const uint PromptRuleSize = 18;

    /// <summary>Eighths of a point, matching the 2px edge the HTML export draws on a block quote.</summary>
    private const uint QuoteRuleSize = 12;

    private readonly IMarkdownBlockMapper _mapper = mapper;

    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Docx;

    /// <inheritdoc />
    public ConversationExport Render(ConversationExportDocument document)
    {
        using var stream = new MemoryStream();

        // autoSave: false — the package is closed explicitly below, and leaving it on writes the
        // parts a second time on Dispose.
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: false))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Document(new Body());

            main.AddNewPart<StyleDefinitionsPart>().Styles = BuildStyles();
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = BuildNumbering();

            var body = main.Document.Body!;

            body.AppendChild(StyledParagraph(StyleIds.Title, [new ExportRun(document.Name)], main));
            body.AppendChild(StyledParagraph(
                StyleIds.Subtitle,
                [new ExportRun($"Exported {document.ExportedAt.ToString("u", CultureInfo.InvariantCulture)}")],
                main));

            foreach (var message in document.Messages)
            {
                // A prompt is banded rather than bubbled — the same tint and left rule the HTML and
                // PDF exports draw, because a right-aligned bubble does not survive page flow.
                var style = message.Role is ChatRoles.User ? ExportBlockStyle.Prompt : ExportBlockStyle.None;

                body.AppendChild(StyledParagraph(
                    StyleIds.MessageLabel, [new ExportRun(message.Label)], main, style: style));

                foreach (var block in _mapper.Map(message.Markdown))
                {
                    AppendBlock(body, block, main, indentLevel: 0, style);
                }
            }

            main.Document.Save();
            word.Save();
        }

        return new ConversationExport(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            $"{ExportFileNames.Stem(document.Name)}.docx");
    }

    private void AppendBlock(
        OpenXmlCompositeElement parent,
        ExportBlock block,
        MainDocumentPart main,
        int indentLevel,
        ExportBlockStyle style)
    {
        switch (block)
        {
            case HeadingBlock heading:
                parent.AppendChild(StyledParagraph(
                    $"Heading{Math.Clamp(heading.Level, 1, 6)}", heading.Runs, main, indentLevel, style));
                break;

            case ParagraphBlock paragraph:
                parent.AppendChild(StyledParagraph(
                    StyleIds.Normal, paragraph.Runs, main, indentLevel, style));
                break;

            case CodeBlock code:
                AppendCode(parent, code, indentLevel, style);
                break;

            case QuoteBlock quote:
                foreach (var child in quote.Children)
                {
                    AppendBlock(parent, child, main, indentLevel + 1, style | ExportBlockStyle.Quote);
                }
                break;

            case ListBlock list:
                AppendList(parent, list, main, indentLevel, style);
                break;

            case TableBlock table:
                AppendTable(parent, table, main, indentLevel, style);
                break;

            case ThematicBreakBlock:
                parent.AppendChild(HorizontalRule());
                break;

            default:
                break;
        }
    }

    // One Word paragraph per line, because a code block's line breaks are content and Word's own
    // wrapping is not. Shading is on the paragraph rather than the runs so a short line still reads
    // as part of the block.
    private static void AppendCode(
        OpenXmlCompositeElement parent, CodeBlock code, int indentLevel, ExportBlockStyle style)
    {
        // The fence's language, the way the app labels a code block and the HTML export repeats.
        if (!string.IsNullOrWhiteSpace(code.Language))
        {
            var label = new Paragraph(Properties(StyleIds.CodeLanguage, indentLevel, style));
            label.AppendChild(new Run(
                new W.Text(code.Language) { Space = SpaceProcessingModeValues.Preserve }));
            parent.AppendChild(label);
        }

        var lines = code.Text.ReplaceLineEndings("\n").Split('\n');

        foreach (var line in lines)
        {
            var paragraph = new Paragraph(Properties(StyleIds.Code, indentLevel, style));

            // A blank line still needs a paragraph, or the block silently loses its spacing.
            if (line.Length > 0)
            {
                paragraph.AppendChild(new Run(
                    new RunProperties(Monospace()),
                    new W.Text(line) { Space = SpaceProcessingModeValues.Preserve }));
            }

            parent.AppendChild(paragraph);
        }
    }

    private void AppendList(
        OpenXmlCompositeElement parent,
        ListBlock list,
        MainDocumentPart main,
        int indentLevel,
        ExportBlockStyle style)
    {
        // Word's numbering levels stop at nine, and a list nested deeper than this is unreadable in
        // any case; past the cap the items keep their text and share the deepest level.
        var level = Math.Min(indentLevel, MaxListDepth);

        foreach (var item in list.Items)
        {
            // The marker belongs to the item, so it is drawn whether or not the item opens with a
            // paragraph to hang it on — an item starting with a nested list or a fence would
            // otherwise lose its bullet, which an HTML <li> never does. Everything after the first
            // paragraph is indented rather than numbered: a continuation line carrying its own
            // number restarts the count against it.
            var opening = item.Children.FirstOrDefault() as ParagraphBlock;

            parent.AppendChild(NumberedParagraph(
                opening?.Runs ?? [], main, list.Ordered, level, style));

            foreach (var child in item.Children.Skip(opening is null ? 0 : 1))
            {
                AppendBlock(parent, child, main, indentLevel + 1, style);
            }
        }
    }

    private Paragraph NumberedParagraph(
        IReadOnlyList<ExportRun> runs, MainDocumentPart main, bool ordered, int level, ExportBlockStyle style)
    {
        // No paragraph indent: the numbering level carries its own, and one set here would override
        // it and pull every bullet back to the margin.
        var paragraph = new Paragraph(Properties(
            StyleIds.ListParagraph,
            indentLevel: 0,
            style,
            new NumberingProperties(
                new NumberingLevelReference { Val = level },
                new NumberingId { Val = ordered ? OrderedNumberingId : BulletNumberingId })));

        AppendRuns(paragraph, runs, main);

        return paragraph;
    }

    private void AppendTable(
        OpenXmlCompositeElement parent,
        TableBlock table,
        MainDocumentPart main,
        int indentLevel,
        ExportBlockStyle style)
    {
        var rows = table.HeaderRow is null ? table.Rows : [table.HeaderRow, .. table.Rows];

        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(row => row.Cells.Count);

        // A row with no cells is a row Word refuses to open, and the schema permits it — so the
        // validator would not catch this one.
        if (columns == 0)
        {
            return;
        }

        // CT_TblPrBase orders tblStyle, tblW, tblInd, then tblBorders. The indent is what a nested
        // table gets in place of the ancestor CSS gives the HTML export.
        var properties = new TableProperties(
            new TableStyle { Val = StyleIds.TableGrid },
            // Auto width over the full text column: a fixed grid computed here would be wrong at
            // any page size other than the one it was computed for.
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" });

        if (indentLevel > 0)
        {
            properties.AppendChild(new TableIndentation
            {
                Width = indentLevel * IndentStepTwips,
                Type = TableWidthUnitValues.Dxa
            });
        }

        properties.AppendChild(
                // Child order is fixed by CT_TblBorders — top, left, bottom, right, then the two
                // inside edges. A package with them in any other order opens in this SDK and is
                // rejected by Word, which is what the schema-validation test exists to catch.
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = ExportTheme.Colors.Border },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = ExportTheme.Colors.Border },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = ExportTheme.Colors.Border },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = ExportTheme.Colors.Border },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = ExportTheme.Colors.Border },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = ExportTheme.Colors.Border }));

        var wordTable = new W.Table(properties);

        var grid = new TableGrid();
        for (var column = 0; column < columns; column++)
        {
            grid.AppendChild(new GridColumn());
        }

        wordTable.AppendChild(grid);

        for (var index = 0; index < rows.Count; index++)
        {
            var isHeader = table.HeaderRow is not null && index == 0;
            var row = new W.TableRow();

            for (var column = 0; column < columns; column++)
            {
                var cellRuns = column < rows[index].Cells.Count ? rows[index].Cells[column] : [];
                var paragraph = new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = StyleIds.TableCellText }));

                AppendRuns(
                    paragraph,
                    isHeader
                        ? [.. cellRuns.Select(run => run with { Styles = run.Styles | ExportRunStyles.Bold })]
                        : cellRuns,
                    main);

                // CT_TcPr puts tcW before shd. A table inside a prompt is filled cell by cell,
                // because Word has no ancestor for the band to come from the way the HTML has.
                var cellProperties = new TableCellProperties(
                    new TableCellWidth { Type = TableWidthUnitValues.Auto });

                var fill = isHeader
                    ? ExportTheme.Colors.SurfaceAlt
                    : style.HasFlag(ExportBlockStyle.Prompt) ? ExportTheme.Colors.UserBand : null;

                if (fill is not null)
                {
                    cellProperties.AppendChild(new Shading
                    {
                        Val = ShadingPatternValues.Clear,
                        Fill = fill
                    });
                }

                row.AppendChild(new W.TableCell(cellProperties, paragraph));
            }

            wordTable.AppendChild(row);
        }

        parent.AppendChild(wordTable);
        // Word requires a paragraph after a table; without one, two adjacent tables merge into one.
        parent.AppendChild(new Paragraph());
    }

    private static Paragraph HorizontalRule() =>
        new(new ParagraphProperties(
            new ParagraphBorders(
                new BottomBorder { Val = BorderValues.Single, Size = 6, Color = ExportTheme.Colors.Border })));

    private Paragraph StyledParagraph(
        string styleId,
        IReadOnlyList<ExportRun> runs,
        MainDocumentPart main,
        int indentLevel = 0,
        ExportBlockStyle style = ExportBlockStyle.None)
    {
        var paragraph = new Paragraph(Properties(styleId, indentLevel, style));

        AppendRuns(paragraph, runs, main);

        return paragraph;
    }

    /// <summary>
    /// Builds one paragraph's properties with its children in schema order.
    /// </summary>
    /// <remarks>
    /// <c>CT_PPrBase</c> fixes the sequence — <c>pStyle</c>, <c>numPr</c>, <c>pBdr</c>, <c>shd</c>,
    /// then <c>ind</c>. A package with them in any other order reads back through this SDK without
    /// complaint and is refused by Word.
    /// </remarks>
    private static ParagraphProperties Properties(
        string styleId, int indentLevel, ExportBlockStyle style, NumberingProperties? numbering = null)
    {
        var properties = new ParagraphProperties(new ParagraphStyleId { Val = styleId });

        if (numbering is not null)
        {
            properties.AppendChild(numbering);
        }

        var prompt = style.HasFlag(ExportBlockStyle.Prompt);

        // Word draws one rule per paragraph, so a quote inside a prompt keeps the prompt's: the
        // outer container is the one a reader loses more by not seeing.
        if (prompt || style.HasFlag(ExportBlockStyle.Quote))
        {
            properties.AppendChild(new ParagraphBorders(
                new LeftBorder
                {
                    Val = BorderValues.Single,
                    Size = prompt ? PromptRuleSize : QuoteRuleSize,
                    Space = 6,
                    Color = prompt ? ExportTheme.Colors.Brand : ExportTheme.Colors.Border
                }));
        }

        // A code block keeps its own fill inside a band, marked by the rule alone: filling it with
        // the band colour would erase the distinction between prose and code.
        if (prompt && !IsCodeStyle(styleId))
        {
            properties.AppendChild(new Shading
            {
                Val = ShadingPatternValues.Clear,
                Fill = ExportTheme.Colors.UserBand
            });
        }

        if (indentLevel > 0)
        {
            properties.AppendChild(new Indentation
            {
                Left = (indentLevel * IndentStepTwips).ToString(CultureInfo.InvariantCulture)
            });
        }

        return properties;
    }

    private static bool IsCodeStyle(string styleId) =>
        string.Equals(styleId, StyleIds.Code, StringComparison.Ordinal)
        || string.Equals(styleId, StyleIds.CodeLanguage, StringComparison.Ordinal);

    private void AppendRuns(Paragraph paragraph, IReadOnlyList<ExportRun> runs, MainDocumentPart main)
    {
        foreach (var run in runs)
        {
            if (run.LinkUrl is null)
            {
                AppendText(paragraph, run);

                continue;
            }

            // A relative or malformed URL cannot become a relationship, so it renders as styled text
            // with no target rather than failing the whole export. The scheme allowlist has already
            // run in MarkdownPipelines, so what reaches here is http, https, mailto or relative.
            if (!Uri.TryCreate(run.LinkUrl, UriKind.Absolute, out var uri))
            {
                AppendText(paragraph, run);

                continue;
            }

            var relationship = main.AddHyperlinkRelationship(uri, isExternal: true);
            var hyperlink = new W.Hyperlink { Id = relationship.Id };
            AppendText(hyperlink, run, StyleIds.HyperlinkChar);
            paragraph.AppendChild(hyperlink);
        }
    }

    private static void AppendText(OpenXmlCompositeElement parent, ExportRun run, string? characterStyleId = null)
    {
        // CT_RPr order: rStyle, rFonts, b, i.
        var properties = new RunProperties();

        if (characterStyleId is not null)
        {
            properties.AppendChild(new RunStyle { Val = characterStyleId });
        }

        if (run.Styles.HasFlag(ExportRunStyles.Code))
        {
            properties.AppendChild(Monospace());
        }

        if (run.Styles.HasFlag(ExportRunStyles.Bold))
        {
            properties.AppendChild(new Bold());
        }

        if (run.Styles.HasFlag(ExportRunStyles.Italic))
        {
            properties.AppendChild(new Italic());
        }

        // A hard break inside a run is a line break in the source, and Word needs the element rather
        // than the character — a raw \n in w:t is normalized to a space.
        var segments = run.Text.ReplaceLineEndings("\n").Split('\n');
        var element = new Run(properties);

        for (var index = 0; index < segments.Length; index++)
        {
            if (index > 0)
            {
                element.AppendChild(new Break());
            }

            element.AppendChild(new W.Text(segments[index]) { Space = SpaceProcessingModeValues.Preserve });
        }

        parent.AppendChild(element);
    }

    private static RunFonts Monospace() =>
        new() { Ascii = ExportTheme.Fonts.WordMono, HighAnsi = ExportTheme.Fonts.WordMono };

    private static int HalfPoints(double points) =>
        (int)Math.Round(points * 2, MidpointRounding.AwayFromZero);

    private static Styles BuildStyles()
    {
        var styles = new Styles();

        // Every other style is based on Normal, so naming the face, the size and the ink here is what
        // puts them on the whole document.
        styles.AppendChild(ParagraphStyle(
            StyleIds.Normal, "Normal", null,
            spaceAfterTwips: 120,
            sizeHalfPoints: HalfPoints(ExportTheme.Sizes.Body),
            color: ExportTheme.Colors.Text,
            fontName: ExportTheme.Fonts.WordSans));

        styles.AppendChild(ParagraphStyle(
            StyleIds.Title, "Title", StyleIds.Normal,
            spaceAfterTwips: 60,
            sizeHalfPoints: HalfPoints(ExportTheme.Sizes.Title),
            bold: true,
            color: ExportTheme.Colors.Brand));

        styles.AppendChild(ParagraphStyle(
            StyleIds.Subtitle, "Subtitle", StyleIds.Normal,
            spaceAfterTwips: 360,
            sizeHalfPoints: HalfPoints(ExportTheme.Sizes.Subtitle),
            color: ExportTheme.Colors.Muted));

        styles.AppendChild(ParagraphStyle(
            StyleIds.MessageLabel, "Message Label", StyleIds.Normal,
            spaceBeforeTwips: 360, spaceAfterTwips: 120,
            sizeHalfPoints: HalfPoints(ExportTheme.Sizes.RoleLabel),
            bold: true,
            color: ExportTheme.Colors.Brand));

        // Six levels because the mapper clamps markdown headings to six; a document that names
        // Heading7 gets Word's default rather than nothing, but it can never be asked for.
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(ParagraphStyle(
                $"Heading{level}", $"heading {level}", StyleIds.Normal,
                spaceBeforeTwips: 240, spaceAfterTwips: 120,
                sizeHalfPoints: HalfPoints(ExportTheme.Sizes.Heading(level)),
                bold: true,
                color: ExportTheme.Colors.Brand));
        }

        styles.AppendChild(ParagraphStyle(
            StyleIds.ListParagraph, "List Paragraph", StyleIds.Normal, spaceAfterTwips: 60));

        styles.AppendChild(ParagraphStyle(
            StyleIds.TableCellText, "Table Cell Text", StyleIds.Normal,
            spaceAfterTwips: 0,
            sizeHalfPoints: HalfPoints(ExportTheme.Sizes.TableCell)));

        styles.AppendChild(ParagraphStyle(
            StyleIds.CodeLanguage, "Code Language", StyleIds.Normal,
            spaceBeforeTwips: 120, spaceAfterTwips: 0,
            sizeHalfPoints: HalfPoints(ExportTheme.Sizes.CodeLanguage),
            color: ExportTheme.Colors.Muted,
            fontName: ExportTheme.Fonts.WordMono,
            shadingFill: ExportTheme.Colors.SurfaceAlt));

        styles.AppendChild(ParagraphStyle(
            StyleIds.Code, "Code Block", StyleIds.Normal,
            spaceAfterTwips: 0,
            sizeHalfPoints: HalfPoints(ExportTheme.Sizes.Code),
            fontName: ExportTheme.Fonts.WordMono,
            shadingFill: ExportTheme.Colors.SurfaceAlt));

        styles.AppendChild(new Style(
            new StyleName { Val = "Hyperlink" },
            new StyleRunProperties(
                new W.Color { Val = ExportTheme.Colors.Link },
                new Underline { Val = UnderlineValues.Single }))
        {
            Type = StyleValues.Character,
            StyleId = StyleIds.HyperlinkChar
        });

        styles.AppendChild(new Style(new StyleName { Val = "Table Grid" })
        {
            Type = StyleValues.Table,
            StyleId = StyleIds.TableGrid
        });

        return styles;
    }

    /// <summary>
    /// Builds one paragraph style with its children in schema order.
    /// </summary>
    /// <remarks>
    /// The order is not cosmetic and not the SDK's business to fix: <c>CT_PPrBase</c> puts <c>shd</c>
    /// before <c>spacing</c>, and <c>CT_RPr</c> puts <c>rFonts</c>, <c>b</c>, <c>color</c> and
    /// <c>sz</c> in that sequence. Appending them in a different one produces a package this SDK
    /// reads back happily and Word refuses to open, which is why the styles are assembled here rather
    /// than mutated by their callers afterwards.
    /// </remarks>
    private static Style ParagraphStyle(
        string styleId,
        string name,
        string? basedOn,
        int spaceAfterTwips,
        int spaceBeforeTwips = 0,
        int? sizeHalfPoints = null,
        bool bold = false,
        string? color = null,
        string? fontName = null,
        string? shadingFill = null)
    {
        var paragraphProperties = new StyleParagraphProperties();

        if (shadingFill is not null)
        {
            paragraphProperties.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Fill = shadingFill });
        }

        paragraphProperties.AppendChild(new SpacingBetweenLines
        {
            After = spaceAfterTwips.ToString(CultureInfo.InvariantCulture),
            Before = spaceBeforeTwips.ToString(CultureInfo.InvariantCulture)
        });

        var runProperties = new StyleRunProperties();

        if (fontName is not null)
        {
            runProperties.AppendChild(new RunFonts { Ascii = fontName, HighAnsi = fontName });
        }

        if (bold)
        {
            runProperties.AppendChild(new Bold());
        }

        if (color is not null)
        {
            runProperties.AppendChild(new W.Color { Val = color });
        }

        if (sizeHalfPoints is int size)
        {
            runProperties.AppendChild(new FontSize { Val = size.ToString(CultureInfo.InvariantCulture) });
        }

        var style = new Style(new StyleName { Val = name }, paragraphProperties, runProperties)
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        };

        if (basedOn is not null)
        {
            style.InsertAfter(new BasedOn { Val = basedOn }, style.StyleName);
        }

        return style;
    }

    private static Numbering BuildNumbering()
    {
        var numbering = new Numbering();

        // Word resolves w:num -> w:abstractNum by id, and the order of the two element kinds inside
        // w:numbering is fixed by the schema: every w:abstractNum first, then every w:num.
        numbering.AppendChild(AbstractNumbering(BulletNumberingId, ordered: false));
        numbering.AppendChild(AbstractNumbering(OrderedNumberingId, ordered: true));
        numbering.AppendChild(new NumberingInstance(new AbstractNumId { Val = BulletNumberingId }) { NumberID = BulletNumberingId });
        numbering.AppendChild(new NumberingInstance(new AbstractNumId { Val = OrderedNumberingId }) { NumberID = OrderedNumberingId });

        return numbering;
    }

    private static AbstractNum AbstractNumbering(int id, bool ordered)
    {
        var abstractNum = new AbstractNum { AbstractNumberId = id };

        // The bullet glyphs Word itself cycles through, and the same three-cycle for numbers.
        var bullets = new[] { "•", "◦", "▪" };
        var numberFormats = new[] { NumberFormatValues.Decimal, NumberFormatValues.LowerLetter, NumberFormatValues.LowerRoman };

        for (var level = 0; level <= MaxListDepth; level++)
        {
            abstractNum.AppendChild(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat
                {
                    Val = ordered ? numberFormats[level % numberFormats.Length] : NumberFormatValues.Bullet
                },
                new LevelText { Val = ordered ? $"%{level + 1}." : bullets[level % bullets.Length] },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(
                    new Indentation
                    {
                        Left = ((level + 1) * IndentStepTwips + IndentStepTwips).ToString(CultureInfo.InvariantCulture),
                        Hanging = IndentStepTwips.ToString(CultureInfo.InvariantCulture)
                    }))
            {
                LevelIndex = level
            });
        }

        return abstractNum;
    }

    private static class StyleIds
    {
        public const string Normal = "Normal";
        public const string Title = "ExportTitle";
        public const string Subtitle = "ExportSubtitle";
        public const string MessageLabel = "ExportMessageLabel";
        public const string Code = "ExportCode";
        public const string CodeLanguage = "ExportCodeLanguage";
        public const string ListParagraph = "ListParagraph";
        public const string TableCellText = "ExportTableCell";
        public const string TableGrid = "ExportTableGrid";
        public const string HyperlinkChar = "Hyperlink";
    }
}
