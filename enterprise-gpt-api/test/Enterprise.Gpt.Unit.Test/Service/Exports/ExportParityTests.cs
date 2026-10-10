using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Export.Renderers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

/// <summary>
/// One conversation, every format, the same document.
/// </summary>
/// <remarks>
/// Each renderer walks the block model with its own switch, so nothing but a test stops the three
/// from drifting apart on an arm one of them forgot. Rendered text is compared rather than layout:
/// what has to hold is that no format silently loses a heading, a list item, a table cell or a line
/// of code the others keep.
/// </remarks>
[Collection(PdfFontCollection.Name)]
public sealed partial class ExportParityTests(PdfFontFixture fonts)
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 8, 19, 9, 30, 0, TimeSpan.Zero);

    private const string Prompt = "Compare the two **options** for me.";

    private const string Answer = """
        ## Findings

        The **first** option is _cheaper_, and `Alpha` is its identifier. See [the docs](https://example.test/docs).

        ```ts
        const total = 1;
        ```

        1. First step
        2. Second step
           - Nested point

        | Option | Verdict |
        | --- | --- |
        | Alpha | Keep |
        | Beta | Drop |

        > Quoted aside.

        ---
        """;

    /// <summary>
    /// Text every rendering of the fixture has to carry. Markdown is the source, so a fragment here
    /// must survive both being emitted verbatim and being walked into blocks.
    /// </summary>
    public static TheoryData<string> SharedText =>
    [
        "Planning notes",
        "Exported 2026-08-19 09:30:00Z",
        "You",
        "Assistant",
        "options",
        "Findings",
        "cheaper",
        "Alpha",
        "the docs",
        "const total = 1;",
        "First step",
        "Nested point",
        "Verdict",
        "Beta",
        "Quoted aside."
    ];

    /// <summary>
    /// One markdown fixture per arm of the block model, so a renderer that silently drops one is a
    /// failing test rather than a document quietly missing its tables.
    /// </summary>
    public static TheoryData<string, string> EveryBlockKind =>
    [
        ("heading", "# A heading"),
        ("paragraph", "A paragraph."),
        ("code", "```\nconst a = 1;\n```"),
        ("quote", "> A quotation."),
        ("list", "- An item"),
        ("table", "| a | b |\n| --- | --- |\n| 1 | 2 |"),
        ("thematic break", "---")
    ];

    private readonly PdfFontFixture _fonts = fonts;

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex("<w:pBdr>\\s*<w:left[^>]*w:color=\"(?<colour>[0-9A-F]{6})\"")]
    private static partial Regex ParagraphRule();

    private static ConversationExportDocument OneMessage(string markdown)
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var created = ExportedAt.AddMinutes(-1);

        return new ConversationExportDocument(
            TranscriptHeaderDocument.Create(userId, conversationId, "One", created),
            [
                TranscriptMessageDocument.Create(
                    userId, conversationId, Guid.NewGuid(), ChatRoles.Assistant, markdown, created)
            ],
            ExportedAt);
    }

    private static ConversationExportDocument Fixture()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var created = ExportedAt.AddMinutes(-10);

        var header = TranscriptHeaderDocument.Create(userId, conversationId, "Planning notes", created);

        TranscriptMessageDocument[] messages =
        [
            TranscriptMessageDocument.Create(
                userId, conversationId, Guid.NewGuid(), ChatRoles.User, Prompt, created),
            TranscriptMessageDocument.Create(
                userId, conversationId, Guid.NewGuid(), ChatRoles.Assistant, Answer, created.AddSeconds(5))
        ];

        return new ConversationExportDocument(header, messages, ExportedAt);
    }

    private static string Html() =>
        Encoding.UTF8.GetString(
            new HtmlExportRenderer(new MarkdownBlockMapper()).Render(Fixture()).Content);

    private static string WordXml()
    {
        using var stream = new MemoryStream(
            new WordExportRenderer(new MarkdownBlockMapper()).Render(Fixture()).Content);
        using var word = WordprocessingDocument.Open(stream, isEditable: false);

        return word.MainDocumentPart!.Document!.OuterXml;
    }

    private static string HtmlText()
    {
        var html = Encoding.UTF8.GetString(
            new HtmlExportRenderer(new MarkdownBlockMapper()).Render(Fixture()).Content);

        // From the document header down: the template's own <style> block would otherwise answer
        // for text nobody reads, and <title> would answer for the conversation's name twice over.
        var body = html.IndexOf("<header", StringComparison.Ordinal);

        return WebUtility.HtmlDecode(Tag().Replace(html[body..], " "));
    }

    private static string MarkdownText() =>
        Encoding.UTF8.GetString(new MarkdownExportRenderer().Render(Fixture()).Content);

    private static string WordText() =>
        WordTextOf(new WordExportRenderer(new MarkdownBlockMapper()).Render(Fixture()).Content);

    /// <summary>
    /// The page content streams, which is where a PDF says what it drew.
    /// </summary>
    /// <remarks>
    /// The bytes of the file itself carry a creation timestamp, so comparing them would report every
    /// pair of renders as different and pass whatever it was asked.
    /// </remarks>
    private static string PdfContent(byte[] content)
    {
        using var input = new MemoryStream(content);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var builder = new StringBuilder();

        foreach (PdfPage page in document.Pages)
        {
            builder.Append(
                Encoding.Latin1.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue));
        }

        return builder.ToString();
    }

    private static string WordTextOf(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var word = WordprocessingDocument.Open(stream, isEditable: false);

        return word.MainDocumentPart!.Document!.Body!.InnerText;
    }

    private static string WordXmlOf(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var word = WordprocessingDocument.Open(stream, isEditable: false);

        return word.MainDocumentPart!.Document!.OuterXml;
    }

    /// <summary>
    /// Each renderer walks the block model with a switch of its own, and a missing arm falls through
    /// to <c>default</c> in silence. What proves the arm is there is that a document holding one
    /// block renders differently from one holding none.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryBlockKind))]
    public void Render_EveryFormat_DrawsEveryBlockKind(string kind, string markdown)
    {
        var mapper = new MarkdownBlockMapper();

        // The fixture has to produce a block, or the rest of this asserts nothing.
        Assert.NotEmpty(mapper.Map(markdown));

        var html = new HtmlExportRenderer(mapper);
        var word = new WordExportRenderer(mapper);

        Assert.False(
            Encoding.UTF8.GetString(html.Render(OneMessage(string.Empty)).Content)
                == Encoding.UTF8.GetString(html.Render(OneMessage(markdown)).Content),
            $"The HTML export drew nothing for a {kind}.");

        // OuterXml rather than InnerText: a thematic break is a bordered empty paragraph and carries
        // no text of its own.
        Assert.False(
            WordXmlOf(word.Render(OneMessage(string.Empty)).Content)
                == WordXmlOf(word.Render(OneMessage(markdown)).Content),
            $"The Word export drew nothing for a {kind}.");

        Assert.SkipUnless(_fonts.IsUsable, PdfFontFixture.SkipReason);

        var pdf = new PdfExportRenderer(mapper);

        Assert.False(
            PdfContent(pdf.Render(OneMessage(string.Empty)).Content)
                == PdfContent(pdf.Render(OneMessage(markdown)).Content),
            $"The PDF export drew nothing for a {kind}.");
    }

    [Theory]
    [MemberData(nameof(SharedText))]
    public void Render_Markdown_CarriesTheSharedText(string fragment)
    {
        Assert.Contains(fragment, MarkdownText(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SharedText))]
    public void Render_Html_CarriesTheSharedText(string fragment)
    {
        Assert.Contains(fragment, HtmlText(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SharedText))]
    public void Render_Word_CarriesTheSharedText(string fragment)
    {
        Assert.Contains(fragment, WordText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// PDF has no text layer this test can read back, so what it pins is that the same fixture lays
    /// out at all — the format that can fail for a reason unrelated to the conversation.
    /// </summary>
    [Fact]
    public void Render_Pdf_LaysOutTheSameFixture()
    {
        Assert.SkipUnless(_fonts.IsUsable, PdfFontFixture.SkipReason);

        var export = new PdfExportRenderer(new MarkdownBlockMapper()).Render(Fixture());

        Assert.Equal("application/pdf", export.ContentType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(export.Content, 0, 4), StringComparison.Ordinal);
    }

    /// <summary>
    /// The three renderings share an outline as well as a vocabulary: the name, then when it was
    /// exported, then the messages in order under their own labels.
    /// </summary>
    [Theory]
    [InlineData("html")]
    [InlineData("md")]
    [InlineData("docx")]
    public void Render_EveryFormat_OrdersTheDocumentTheSameWay(string format)
    {
        var text = format switch
        {
            "html" => HtmlText(),
            "md" => MarkdownText(),
            _ => WordText()
        };

        var title = text.IndexOf("Planning notes", StringComparison.Ordinal);
        var exported = text.IndexOf("Exported 2026-08-19", StringComparison.Ordinal);
        var prompt = text.IndexOf("options", StringComparison.Ordinal);
        var answer = text.IndexOf("Findings", StringComparison.Ordinal);

        Assert.True(title < exported, "The name comes before the export timestamp.");
        Assert.True(exported < prompt, "The header comes before the first message.");
        Assert.True(prompt < answer, "Messages are in transcript order.");
    }

    /// <summary>
    /// A prompt is set apart in every rendering rather than running on as body text, and a quote is
    /// ruled rather than merely indented — the two places the page formats used to say less than the
    /// HTML did.
    /// </summary>
    [Fact]
    public void Render_Html_MarksThePromptAndTheQuote()
    {
        var html = Html();

        Assert.Contains("class=\"message message--user\"", html, StringComparison.Ordinal);
        Assert.Contains("<blockquote>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Word_MarksThePromptAndTheQuote()
    {
        var xml = WordXml();
        var rules = ParagraphRule().Matches(xml)
            .Select(match => match.Groups["colour"].Value)
            .Distinct()
            .ToList();

        Assert.Contains($"w:fill=\"{ExportTheme.Colors.UserBand}\"", xml, StringComparison.Ordinal);
        Assert.Contains(ExportTheme.Colors.Brand, rules);
        Assert.Contains(ExportTheme.Colors.Border, rules);
    }

    /// <summary>
    /// Every rendered format drops an image and keeps its alt text; only markdown, which is the
    /// source rather than a rendering of it, still carries the URL.
    /// </summary>
    [Theory]
    [InlineData("html")]
    [InlineData("docx")]
    public void Render_RenderedFormat_DropsAnImageAndKeepsItsAltText(string format)
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var header = TranscriptHeaderDocument.Create(userId, conversationId, "Shot", ExportedAt);
        var document = new ConversationExportDocument(
            header,
            [
                TranscriptMessageDocument.Create(
                    userId, conversationId, Guid.NewGuid(), ChatRoles.Assistant,
                    "![a diagram](https://example.test/a.png)", ExportedAt)
            ],
            ExportedAt);

        var mapper = new MarkdownBlockMapper();
        var text = format == "html"
            ? WebUtility.HtmlDecode(Tag().Replace(
                Encoding.UTF8.GetString(new HtmlExportRenderer(mapper).Render(document).Content), " "))
            : WordTextOf(new WordExportRenderer(mapper).Render(document).Content);

        Assert.Contains("a diagram", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a.png", text, StringComparison.Ordinal);
    }

}
