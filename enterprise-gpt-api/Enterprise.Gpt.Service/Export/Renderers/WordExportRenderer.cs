using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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
/// No font is named anywhere except on the monospace runs. A .docx carries no glyphs, so the reader's
/// own theme decides the face — which is why this renderer, unlike the PDF one, has no font
/// prerequisite and can never be the format a deployment lacks.
/// </para>
/// </remarks>
/// <param name="mapper">Maps each message's markdown into blocks.</param>
public sealed class WordExportRenderer(IMarkdownBlockMapper mapper) : IConversationExportRenderer
{
    private const string MonospaceFont = "Consolas";
    private const int BulletNumberingId = 1;
    private const int OrderedNumberingId = 2;
    private const int MaxListDepth = 8;

    /// <summary>Twips of indent per list or quote level. 360 is Word's own half-inch-per-two-levels.</summary>
    private const int IndentStepTwips = 360;

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
                body.AppendChild(StyledParagraph(StyleIds.MessageLabel, [new ExportRun(message.Label)], main));

                foreach (var block in _mapper.Map(message.Markdown))
                {
                    AppendBlock(body, block, main, indentLevel: 0);
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

    private void AppendBlock(OpenXmlCompositeElement parent, ExportBlock block, MainDocumentPart main, int indentLevel)
    {
        switch (block)
        {
            case HeadingBlock heading:
                parent.AppendChild(StyledParagraph($"Heading{heading.Level}", heading.Runs, main, indentLevel));
                break;

            case ParagraphBlock paragraph:
                parent.AppendChild(StyledParagraph(StyleIds.Normal, paragraph.Runs, main, indentLevel));
                break;

            case CodeBlock code:
                AppendCode(parent, code, indentLevel);
                break;

            case QuoteBlock quote:
                foreach (var child in quote.Children)
                {
                    AppendBlock(parent, child, main, indentLevel + 1);
                }
                break;

            case ListBlock list:
                AppendList(parent, list, main, indentLevel);
                break;

            case TableBlock table:
                AppendTable(parent, table, main);
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
    private static void AppendCode(OpenXmlCompositeElement parent, CodeBlock code, int indentLevel)
    {
        var lines = code.Text.ReplaceLineEndings("\n").Split('\n');

        foreach (var line in lines)
        {
            var paragraph = new Paragraph(Properties(StyleIds.Code, indentLevel));

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

    private void AppendList(OpenXmlCompositeElement parent, ListBlock list, MainDocumentPart main, int indentLevel)
    {
        // Word's numbering levels stop at nine, and a list nested deeper than this is unreadable in
        // any case; past the cap the items keep their text and share the deepest level.
        var level = Math.Min(indentLevel, MaxListDepth);

        foreach (var item in list.Items)
        {
            var first = true;

            foreach (var child in item.Children)
            {
                // Only the item's first paragraph gets the bullet or number. A second paragraph, a
                // nested list or a code block inside the same item is indented to match instead —
                // numbering them would restart the count at every continuation line.
                if (first && child is ParagraphBlock paragraph)
                {
                    parent.AppendChild(NumberedParagraph(paragraph.Runs, main, list.Ordered, level));
                    first = false;

                    continue;
                }

                AppendBlock(parent, child, main, indentLevel + 1);
                first = false;
            }

            // An empty item still occupies a line, or the numbering skips a value with nothing to
            // show for it.
            if (item.Children.Count == 0)
            {
                parent.AppendChild(NumberedParagraph([], main, list.Ordered, level));
            }
        }
    }

    private Paragraph NumberedParagraph(
        IReadOnlyList<ExportRun> runs, MainDocumentPart main, bool ordered, int level)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = StyleIds.ListParagraph },
                new NumberingProperties(
                    new NumberingLevelReference { Val = level },
                    new NumberingId { Val = ordered ? OrderedNumberingId : BulletNumberingId })));

        AppendRuns(paragraph, runs, main);

        return paragraph;
    }

    private void AppendTable(OpenXmlCompositeElement parent, TableBlock table, MainDocumentPart main)
    {
        var rows = table.HeaderRow is null ? table.Rows : [table.HeaderRow, .. table.Rows];

        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(row => row.Cells.Count);

        var wordTable = new W.Table(
            new TableProperties(
                new TableStyle { Val = StyleIds.TableGrid },
                // Auto width over the full text column: a fixed grid computed here would be wrong at
                // any page size other than the one it was computed for.
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                // Child order is fixed by CT_TblBorders — top, left, bottom, right, then the two
                // inside edges. A package with them in any other order opens in this SDK and is
                // rejected by Word, which is what the schema-validation test exists to catch.
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" })));

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

                row.AppendChild(new W.TableCell(
                    new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto }),
                    paragraph));
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
                new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "D0D7DE" })));

    private Paragraph StyledParagraph(
        string styleId, IReadOnlyList<ExportRun> runs, MainDocumentPart main, int indentLevel = 0)
    {
        var paragraph = new Paragraph(Properties(styleId, indentLevel));

        AppendRuns(paragraph, runs, main);

        return paragraph;
    }

    private static ParagraphProperties Properties(string styleId, int indentLevel)
    {
        var properties = new ParagraphProperties(new ParagraphStyleId { Val = styleId });

        if (indentLevel > 0)
        {
            properties.AppendChild(new Indentation
            {
                Left = (indentLevel * IndentStepTwips).ToString(CultureInfo.InvariantCulture)
            });
        }

        return properties;
    }

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
        var properties = new RunProperties();

        if (characterStyleId is not null)
        {
            properties.AppendChild(new RunStyle { Val = characterStyleId });
        }

        if (run.Styles.HasFlag(ExportRunStyles.Bold))
        {
            properties.AppendChild(new Bold());
        }

        if (run.Styles.HasFlag(ExportRunStyles.Italic))
        {
            properties.AppendChild(new Italic());
        }

        if (run.Styles.HasFlag(ExportRunStyles.Code))
        {
            properties.AppendChild(Monospace());
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
        new() { Ascii = MonospaceFont, HighAnsi = MonospaceFont };

    private static Styles BuildStyles()
    {
        var styles = new Styles();

        styles.AppendChild(ParagraphStyle(StyleIds.Normal, "Normal", null, spaceAfterTwips: 120));
        styles.AppendChild(ParagraphStyle(StyleIds.Title, "Title", StyleIds.Normal, spaceAfterTwips: 240, sizeHalfPoints: 40, bold: true));
        styles.AppendChild(ParagraphStyle(StyleIds.Subtitle, "Subtitle", StyleIds.Normal, spaceAfterTwips: 360, sizeHalfPoints: 18, color: "5A6E82"));
        styles.AppendChild(ParagraphStyle(StyleIds.MessageLabel, "Message Label", StyleIds.Normal, spaceBeforeTwips: 360, spaceAfterTwips: 120, sizeHalfPoints: 22, bold: true, color: "14324F"));


        // Six levels because the mapper clamps markdown headings to six; a document that names
        // Heading7 gets Word's default rather than nothing, but it can never be asked for.
        var headingSizes = new[] { 32, 28, 26, 24, 22, 22 };
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(ParagraphStyle(
                $"Heading{level}", $"heading {level}", StyleIds.Normal,
                spaceBeforeTwips: 240, spaceAfterTwips: 120,
                sizeHalfPoints: headingSizes[level - 1], bold: true));
        }

        styles.AppendChild(ParagraphStyle(StyleIds.ListParagraph, "List Paragraph", StyleIds.Normal, spaceAfterTwips: 60));
        styles.AppendChild(ParagraphStyle(StyleIds.TableCellText, "Table Cell Text", StyleIds.Normal, spaceAfterTwips: 0));

        styles.AppendChild(ParagraphStyle(
            StyleIds.Code, "Code Block", StyleIds.Normal,
            spaceAfterTwips: 0, sizeHalfPoints: 18, monospace: true, shadingFill: "F3F5F7"));

        styles.AppendChild(new Style(
            new StyleName { Val = "Hyperlink" },
            new StyleRunProperties(
                new W.Color { Val = "0B5FA5" },
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
    /// before <c>spacing</c>, and <c>CT_RPr</c> puts <c>rFonts</c>, <c>b</c>, <c>i</c>, <c>color</c>
    /// and <c>sz</c> in that sequence. Appending them in a different one produces a package this SDK
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
        bool monospace = false,
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

        if (monospace)
        {
            runProperties.AppendChild(Monospace());
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
        public const string ListParagraph = "ListParagraph";
        public const string TableCellText = "ExportTableCell";
        public const string TableGrid = "ExportTableGrid";
        public const string HyperlinkChar = "Hyperlink";
    }
}
