using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Enterprise.Gpt.Service.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using Xunit;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Covers the check every produced artifact passes before it is stored: that it opens with the library
/// that owns its format, is not empty, and reports what it holds.
/// </summary>
/// <remarks>
/// Every fixture is built here rather than committed, so a format's expectations and the bytes that
/// meet them cannot drift apart, and so a failure names the shape rather than a checked-in blob.
/// </remarks>
public sealed class GeneratedArtifactVerifierTests
{
    private readonly GeneratedArtifactVerifier _verifier =
        new(NullLogger<GeneratedArtifactVerifier>.Instance);

    [Fact]
    public void Verify_WordDocument_ReportsItsParagraphsAndTables()
    {
        var result = _verifier.Verify("notes.docx", WordDocument("Hello", "World"));

        Assert.True(result.Passed);
        Assert.Equal("2 paragraphs, 0 tables", result.Shape);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Verify_Workbook_ReportsItsSheets()
    {
        var result = _verifier.Verify("figures.xlsx", Workbook("Summary", "Detail"));

        Assert.True(result.Passed);
        Assert.Equal("2 sheets (Summary, Detail)", result.Shape);
    }

    [Fact]
    public void Verify_Presentation_ReportsItsSlides()
    {
        var result = _verifier.Verify("deck.pptx", Presentation(slides: 3));

        Assert.True(result.Passed);
        Assert.Equal("3 slides", result.Shape);
    }

    [Fact]
    public void Verify_Pdf_ReportsItsPages()
    {
        var result = _verifier.Verify("report.pdf", Pdf(pages: 2));

        Assert.True(result.Passed);
        Assert.Equal("2 pages", result.Shape);
    }

    [Fact]
    public void Verify_Csv_ReportsItsHeaderAndRows()
    {
        var result = _verifier.Verify("rows.csv", Bytes("name,value\na,1\nb,2\n"));

        Assert.True(result.Passed);
        Assert.Equal("2 columns, 2 data rows", result.Shape);
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData("notes.txt")]
    public void Verify_Text_ReportsItsLength(string fileName)
    {
        var result = _verifier.Verify(fileName, Bytes("# Title"));

        Assert.True(result.Passed);
        Assert.Equal("7 characters", result.Shape);
    }

    [Theory]
    [InlineData("notes.docx")]
    [InlineData("figures.xlsx")]
    [InlineData("deck.pptx")]
    [InlineData("report.pdf")]
    public void Verify_BytesThatAreNotTheFormatTheyClaim_Fails(string fileName)
    {
        var result = _verifier.Verify(fileName, Bytes("this is not an office document at all"));

        Assert.False(result.Passed);
        Assert.Equal(string.Empty, result.Shape);
        Assert.NotNull(result.Reason);
    }

    // A zip that opens as a package but carries no document part: the failure mode a truncated write
    // produces, and the one a "does it unzip" check would wave through.
    [Fact]
    public void Verify_OfficePackageWithNoDocument_Fails()
    {
        var result = _verifier.Verify("notes.docx", Bytes("PK not really"));

        Assert.False(result.Passed);
    }

    [Fact]
    public void Verify_EmptyBytes_FailsBeforeAnyParserRuns()
    {
        var result = _verifier.Verify("notes.docx", []);

        Assert.False(result.Passed);
        Assert.Equal("it is empty", result.Reason);
    }

    [Fact]
    public void Verify_WordDocumentWithNoContent_Fails()
    {
        var result = _verifier.Verify("notes.docx", WordDocument());

        Assert.False(result.Passed);
    }

    [Fact]
    public void Verify_CsvWithNoHeaderRow_Fails()
    {
        var result = _verifier.Verify("rows.csv", Bytes("\n"));

        Assert.False(result.Passed);
    }

    [Fact]
    public void Verify_TextThatIsOnlyWhitespace_Fails()
    {
        var result = _verifier.Verify("notes.txt", Bytes("   \n\t "));

        Assert.False(result.Passed);
    }

    // A replacement-character decode would let a file this platform wrote come back as mojibake in the
    // user's editor and still be reported as fine.
    [Fact]
    public void Verify_TextThatIsNotUtf8_Fails()
    {
        var result = _verifier.Verify("notes.txt", [0xC3, 0x28]);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Verify_AnExtensionThisPlatformDoesNotProduce_Fails()
    {
        var result = _verifier.Verify("macro.xlsm", Bytes("anything"));

        Assert.False(result.Passed);
        Assert.Equal("it is not a format this platform produces", result.Reason);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] WordDocument(params string[] paragraphs)
    {
        using var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var body = new Body();

            foreach (var text in paragraphs)
            {
                body.AppendChild(new Paragraph(new Run(new Text(text))));
            }

            document.AddMainDocumentPart().Document = new Document(body);
        }

        return stream.ToArray();
    }

    private static byte[] Workbook(params string[] sheetNames)
    {
        using var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            for (var index = 0; index < sheetNames.Length; index++)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new Worksheet(new SheetData());

                sheets.AppendChild(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = (uint)(index + 1),
                    Name = sheetNames[index]
                });
            }
        }

        return stream.ToArray();
    }

    private static byte[] Presentation(int slides)
    {
        using var stream = new MemoryStream();

        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new DocumentFormat.OpenXml.Presentation.Presentation(new SlideIdList());
            var slideIdList = presentationPart.Presentation.SlideIdList!;

            for (var index = 0; index < slides; index++)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree()));

                slideIdList.AppendChild(new SlideId
                {
                    Id = (uint)(256 + index),
                    RelationshipId = presentationPart.GetIdOfPart(slidePart)
                });
            }
        }

        return stream.ToArray();
    }

    private static byte[] Pdf(int pages)
    {
        using var document = new PdfDocument();

        for (var index = 0; index < pages; index++)
        {
            document.AddPage();
        }

        using var stream = new MemoryStream();
        document.Save(stream);

        return stream.ToArray();
    }
}
