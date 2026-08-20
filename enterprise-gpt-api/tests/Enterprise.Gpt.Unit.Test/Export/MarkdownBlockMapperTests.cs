using Enterprise.Gpt.Service.Export;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

public sealed class MarkdownBlockMapperTests
{
    private readonly MarkdownBlockMapper _mapper = new();

    private static string TextOf(IEnumerable<ExportRun> runs) =>
        string.Concat(runs.Select(run => run.Text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Map_EmptyInput_ReturnsNoBlocks(string? markdown)
    {
        Assert.Empty(_mapper.Map(markdown));
    }

    [Fact]
    public void Map_Paragraph_ReturnsOneParagraph()
    {
        var block = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map("Just some prose.")));

        Assert.Equal("Just some prose.", TextOf(block.Runs));
    }

    [Theory]
    [InlineData("# One", 1)]
    [InlineData("### Three", 3)]
    [InlineData("###### Six", 6)]
    public void Map_Heading_KeepsItsLevel(string markdown, int expected)
    {
        var heading = Assert.IsType<HeadingBlock>(Assert.Single(_mapper.Map(markdown)));

        Assert.Equal(expected, heading.Level);
    }

    [Fact]
    public void Map_Emphasis_CarriesTheStyleOnItsRun()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map("plain **bold** _italic_ `code`")));

        Assert.Contains(paragraph.Runs, run => run.Text == "bold" && run.Styles == ExportRunStyles.Bold);
        Assert.Contains(paragraph.Runs, run => run.Text == "italic" && run.Styles == ExportRunStyles.Italic);
        Assert.Contains(paragraph.Runs, run => run.Text == "code" && run.Styles == ExportRunStyles.Code);
    }

    /// <summary>
    /// Nested emphasis is two flags on one run, not a choice between them — which is why the styles
    /// are a flags enum rather than a single value.
    /// </summary>
    [Fact]
    public void Map_NestedEmphasis_CombinesTheStyles()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map("**bold _and italic_**")));

        Assert.Contains(
            paragraph.Runs,
            run => run.Text == "and italic" && run.Styles == (ExportRunStyles.Bold | ExportRunStyles.Italic));
    }

    /// <summary>
    /// Markdig emits a literal per parse event, so an unstyled sentence arrives as several runs. A
    /// renderer that wrote one element per run would produce a dozen for every ordinary line.
    /// </summary>
    [Fact]
    public void Map_AdjacentTextWithTheSameStyling_IsMergedIntoOneRun()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(
            Assert.Single(_mapper.Map("A sentence with punctuation - and a dash.")));

        Assert.Equal("A sentence with punctuation - and a dash.", Assert.Single(paragraph.Runs).Text);
    }

    [Fact]
    public void Map_FencedCodeBlock_KeepsItsLanguageAndLineBreaks()
    {
        var code = Assert.IsType<CodeBlock>(Assert.Single(_mapper.Map("```csharp\nvar x = 1;\nvar y = 2;\n```")));

        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;\nvar y = 2;", code.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Map_FenceWithNoInfoString_HasNoLanguage()
    {
        var code = Assert.IsType<CodeBlock>(Assert.Single(_mapper.Map("```\nplain\n```")));

        Assert.Null(code.Language);
    }

    [Fact]
    public void Map_BulletList_ReturnsAnUnorderedListOfItems()
    {
        var list = Assert.IsType<ListBlock>(Assert.Single(_mapper.Map("- one\n- two")));

        Assert.False(list.Ordered);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(
            "one",
            TextOf(Assert.IsType<ParagraphBlock>(Assert.Single(list.Items[0].Children)).Runs));
    }

    [Fact]
    public void Map_NumberedList_IsMarkedOrdered()
    {
        var list = Assert.IsType<ListBlock>(Assert.Single(_mapper.Map("1. first\n2. second")));

        Assert.True(list.Ordered);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Map_NestedList_KeepsTheInnerListInsideItsItem()
    {
        var list = Assert.IsType<ListBlock>(Assert.Single(_mapper.Map("- outer\n  - inner")));

        var item = Assert.Single(list.Items);
        Assert.Contains(item.Children, child => child is ListBlock);
    }

    [Fact]
    public void Map_BlockQuote_KeepsItsChildrenNested()
    {
        var quote = Assert.IsType<QuoteBlock>(Assert.Single(_mapper.Map("> quoted text")));

        Assert.Equal("quoted text", TextOf(Assert.IsType<ParagraphBlock>(Assert.Single(quote.Children)).Runs));
    }

    [Fact]
    public void Map_ThematicBreak_ReturnsARule()
    {
        Assert.IsType<ThematicBreakBlock>(Assert.Single(_mapper.Map("---")));
    }

    [Fact]
    public void Map_PipeTable_SeparatesTheHeaderFromTheBody()
    {
        var table = Assert.IsType<TableBlock>(
            Assert.Single(_mapper.Map("| a | b |\n| --- | --- |\n| 1 | 2 |")));

        Assert.NotNull(table.HeaderRow);
        Assert.Equal("a", TextOf(table.HeaderRow!.Cells[0]));
        Assert.Equal("b", TextOf(table.HeaderRow.Cells[1]));

        var row = Assert.Single(table.Rows);
        Assert.Equal("1", TextOf(row.Cells[0]));
        Assert.Equal("2", TextOf(row.Cells[1]));
    }

    [Fact]
    public void Map_Link_CarriesItsUrlOnTheRun()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map("see [the docs](https://example.test/a)")));

        Assert.Contains(paragraph.Runs, run => run.Text == "the docs" && run.LinkUrl == "https://example.test/a");
    }

    /// <summary>
    /// A hyperlink is live in Word and in most PDF readers, so the allowlist that protects the stored
    /// HTML has to protect an exported document too. The text survives; only the target is dropped.
    /// </summary>
    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox)")]
    public void Map_LinkWithADisallowedScheme_KeepsTheTextAndDropsTheTarget(string markdown)
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map(markdown)));

        var run = Assert.Single(paragraph.Runs);
        Assert.Equal("click", run.Text);
        Assert.Null(run.LinkUrl);
    }

    [Fact]
    public void Map_RelativeLink_IsKept()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map("[docs](/help/index.html)")));

        Assert.Equal("/help/index.html", Assert.Single(paragraph.Runs).LinkUrl);
    }

    /// <summary>
    /// Embedding an image means the API fetching a URL that model output chose, from its own network
    /// position. The alt text is kept because it is the only part a reader loses nothing by having.
    /// </summary>
    [Fact]
    public void Map_Image_IsDroppedButKeepsItsAltText()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(
            Assert.Single(_mapper.Map("![a diagram](https://example.test/x.png)")));

        var run = Assert.Single(paragraph.Runs);
        Assert.Equal("a diagram", run.Text);
        Assert.Null(run.LinkUrl);
    }

    /// <summary>
    /// Raw HTML is escaped text under the shared pipeline, never markup — the same refusal that keeps
    /// a script block out of the stored HTML keeps it out of a Word document.
    /// </summary>
    [Fact]
    public void Map_RawHtml_NeverBecomesMarkup()
    {
        var blocks = _mapper.Map("<script>alert(1)</script>\n\nAfter.");

        Assert.DoesNotContain(
            blocks.OfType<ParagraphBlock>().SelectMany(block => block.Runs),
            run => run.LinkUrl is not null);
        Assert.Contains(blocks.OfType<ParagraphBlock>(), block => TextOf(block.Runs).Contains("After.", StringComparison.Ordinal));
    }

    [Fact]
    public void Map_HardLineBreak_BecomesANewlineInsideTheParagraph()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map("first  \nsecond")));

        Assert.Contains("\n", TextOf(paragraph.Runs), StringComparison.Ordinal);
    }

    /// <summary>
    /// Inside Markdig's own 128-level nesting cap, so this really does exercise the recursive walk —
    /// which the 2,000-level case below does not, because the parse fails before the walk begins.
    /// </summary>
    [Fact]
    public void Map_DeeplyNestedInputWithinTheParserLimit_IsWalkedAndFlattenedPastTheCap()
    {
        var markdown = string.Concat(Enumerable.Repeat("> ", 120)) + "deep";

        var blocks = _mapper.Map(markdown);

        // The outermost levels keep their structure; past MaxDepth the text survives and the nesting
        // does not, which is the trade the cap exists to make.
        var quote = Assert.IsType<QuoteBlock>(Assert.Single(blocks));
        Assert.Contains("deep", Flatten(quote), StringComparison.Ordinal);
    }

    /// <summary>
    /// Past Markdig's cap the parse itself throws, and losing the message would be worse than losing
    /// its formatting — so the text comes back as one paragraph.
    /// </summary>
    [Fact]
    public void Map_InputNestedPastTheParserLimit_FallsBackToThePlainText()
    {
        var markdown = string.Concat(Enumerable.Repeat("> ", 2_000)) + "deep";

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map(markdown)));

        Assert.Equal(markdown, TextOf(paragraph.Runs));
    }

    private static string Flatten(ExportBlock block) => block switch
    {
        QuoteBlock quote => string.Concat(quote.Children.Select(Flatten)),
        ParagraphBlock paragraph => TextOf(paragraph.Runs),
        HeadingBlock heading => TextOf(heading.Runs),
        _ => string.Empty
    };

    [Fact]
    public void Map_Autolink_CarriesItsUrl()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(_mapper.Map("<https://example.test/a>")));

        Assert.Equal("https://example.test/a", Assert.Single(paragraph.Runs).LinkUrl);
    }
}
