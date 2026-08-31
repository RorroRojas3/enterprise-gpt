using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Export.Renderers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

/// <summary>
/// The HTML emitter is the third consumer of the block model, and the only one whose output is
/// itself markup — so what it escapes, and what it refuses to emit, is worth pinning.
/// </summary>
public sealed class HtmlBlockWriterTests
{
    private static readonly MarkdownBlockMapper Mapper = new();

    private static string Render(string markdown) => HtmlBlockWriter.Write(Mapper.Map(markdown));

    [Fact]
    public void Write_NoBlocks_IsEmpty()
    {
        Assert.Equal(string.Empty, HtmlBlockWriter.Write([]));
    }

    [Theory]
    [InlineData(1, "h1")]
    [InlineData(3, "h3")]
    [InlineData(6, "h6")]
    public void Write_Heading_UsesItsLevel(int level, string tag)
    {
        var html = Render($"{new string('#', level)} Title");

        Assert.Equal($"<{tag}>Title</{tag}>", html);
    }

    [Fact]
    public void Write_EmphasisAndInlineCode_NestTheirElements()
    {
        var html = Render("**bold** _italic_ `code`");

        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<em>italic</em>", html, StringComparison.Ordinal);
        Assert.Contains("<code>code</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_TextThatLooksLikeMarkup_IsEscaped()
    {
        var html = HtmlBlockWriter.Write(
            [new ParagraphBlock([new ExportRun("1 < 2 & \"quoted\" <script>alert(1)</script>")])]);

        Assert.Equal(
            "<p>1 &lt; 2 &amp; &quot;quoted&quot; &lt;script&gt;alert(1)&lt;/script&gt;</p>",
            html);
    }

    [Fact]
    public void Write_Link_CarriesItsTargetAndOpenerGuard()
    {
        var html = Render("[docs](https://example.test/a?x=1&y=2)");

        Assert.Contains(
            "<a href=\"https://example.test/a?x=1&amp;y=2\" rel=\"noopener noreferrer\">docs</a>",
            html,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A quoted attribute value is the one place an unescaped character would break out of the
    /// element rather than merely render oddly.
    /// </summary>
    [Fact]
    public void Write_LinkUrlContainingAQuote_CannotEscapeTheAttribute()
    {
        var html = HtmlBlockWriter.Write(
            [new ParagraphBlock([new ExportRun("x", ExportRunStyles.None, "https://a.test/\" onmouseover=\"y")])]);

        Assert.DoesNotContain("onmouseover=\"y\"", html, StringComparison.Ordinal);
        Assert.Contains("&quot; onmouseover=&quot;y", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_FencedCode_KeepsItsLinesAndNamesItsLanguage()
    {
        var html = Render("```ts\nconst a = 1 < 2;\nconst b = 2;\n```");

        Assert.Contains("<div class=\"code__lang\">ts</div>", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code>const a = 1 &lt; 2;\nconst b = 2;</code></pre>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_FenceWithNoLanguage_OmitsTheLanguageLine()
    {
        var html = Render("```\nplain\n```");

        Assert.DoesNotContain("code__lang", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code>plain</code></pre>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_NestedList_NestsItsElementsAndKeepsTheFirstParagraphInline()
    {
        var html = Render("- one\n  - deeper\n- two");

        Assert.Equal("<ul><li>one<ul><li>deeper</li></ul></li><li>two</li></ul>", html);
    }

    [Fact]
    public void Write_OrderedList_UsesAnOrderedElement()
    {
        var html = Render("1. one\n2. two");

        Assert.Equal("<ol><li>one</li><li>two</li></ol>", html);
    }

    [Fact]
    public void Write_Table_SeparatesItsHeadFromItsBody()
    {
        var html = Render("| a | b |\n| --- | --- |\n| 1 | 2 |");

        Assert.Equal(
            "<table><thead><tr><th>a</th><th>b</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table>",
            html);
    }

    /// <summary>
    /// A short row is padded rather than truncated; emitting fewer cells would pull every later
    /// column of the table one place left.
    /// </summary>
    [Fact]
    public void Write_RaggedTableRow_IsPaddedToTheWidestRow()
    {
        var html = HtmlBlockWriter.Write(
        [
            new TableBlock(
                new TableRowBlock([[new ExportRun("a")], [new ExportRun("b")]]),
                [new TableRowBlock([[new ExportRun("1")]])])
        ]);

        Assert.Contains("<tr><td>1</td><td></td></tr>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Quote_WrapsItsChildren()
    {
        var html = Render("> quoted");

        Assert.Equal("<blockquote><p>quoted</p></blockquote>", html);
    }

    [Fact]
    public void Write_ThematicBreak_IsARule()
    {
        Assert.Equal("<hr />", Render("---"));
    }

    /// <summary>
    /// The block model has no image arm, so an exported document never asks a reader's browser to
    /// fetch a URL that model output chose.
    /// </summary>
    [Fact]
    public void Write_Image_KeepsItsAltTextAndEmitsNoElement()
    {
        var html = Render("![a diagram](https://example.test/a.png)");

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("a.png", html, StringComparison.Ordinal);
        Assert.Contains("a diagram", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_HardBreak_BecomesALineBreakElement()
    {
        var html = HtmlBlockWriter.Write([new ParagraphBlock([new ExportRun("one\ntwo")])]);

        Assert.Equal("<p>one<br />two</p>", html);
    }
}
