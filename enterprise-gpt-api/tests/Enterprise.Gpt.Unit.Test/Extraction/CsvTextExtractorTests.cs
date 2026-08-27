using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Extraction;

public sealed class CsvTextExtractorTests
{
    private readonly CsvTextExtractor _extractor = Create();

    private static CsvTextExtractor Create(int maxRows = 20_000, int maxColumns = 200, int maxCharacters = 8_000_000)
    {
        var options = Options.Create(new SheetOptions
        {
            MaxRowsPerSheet = maxRows,
            MaxColumnsPerSheet = maxColumns,
            MaxCharactersPerUpload = maxCharacters
        });

        return new CsvTextExtractor(NullLogger<CsvTextExtractor>.Instance, options);
    }

    private static FileDto File(byte[] content, string fileName = "orders.csv")
    {
        return new FileDto { FileName = fileName, ContentType = "text/csv", Length = content.Length, Content = content };
    }

    private static Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
        CsvTextExtractor extractor,
        string content,
        string fileName = "orders.csv",
        IProgress<DocumentExtractionProgress>? progress = null)
    {
        return extractor.ExtractAsync(File(Encoding.UTF8.GetBytes(content), fileName), progress, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void SupportedExtensions_ContainsOnlyCsv()
    {
        Assert.Equal([FileExtensions.Csv], _extractor.SupportedExtensions);
    }

    [Fact]
    public async Task ExtractAsync_CommaDelimitedFile_ReturnsOneSegmentNamedAfterTheFile()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "SKU,Region\nW-1,East\nW-2,West\n", "q3-orders.csv"));

        Assert.Equal(1, segment.SourceNumber);
        Assert.Equal("Sheet: q3-orders\nSKU | Region\nW-1 | East\nW-2 | West", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_SemicolonDelimitedFile_ResolvesItsColumns()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "SKU;Region;Revenue\nW-1;East;1200\nW-2;West;980\n"));

        Assert.Equal("Sheet: orders\nSKU | Region | Revenue\nW-1 | East | 1200\nW-2 | West | 980", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_TabDelimitedFile_ResolvesItsColumns()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "SKU\tRegion\tRevenue\nW-1\tEast\t1200\nW-2\tWest\t980\n"));

        Assert.Equal("Sheet: orders\nSKU | Region | Revenue\nW-1 | East | 1200\nW-2 | West | 980", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_SemicolonDelimitedFileWithDecimalCommas_StillResolvesTheSemicolon()
    {
        // A comma appears on every line as a decimal separator, which is exactly the case a detector that
        // prefers the culture's list separator gets wrong.
        var segment = Assert.Single(await ExtractAsync(_extractor, "Item;Price;Tax\nWidget;1,50;0,30\nGadget;2,75;0,55\n"));

        Assert.Equal("Sheet: orders\nItem | Price | Tax\nWidget | 1,50 | 0,30\nGadget | 2,75 | 0,55", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_QuotedFieldContainingTheDelimiter_KeepsItInOneCell()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "Name,Address\n\"Ltd, Acme\",\"1 High St\"\n"));

        Assert.Equal("Sheet: orders\nName | Address\nLtd, Acme | 1 High St", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_QuotedFieldContainingEscapedQuotes_UnescapesThem()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "Name\n\"He said \"\"hello\"\"\"\n"));

        Assert.Equal("Sheet: orders\nName\nHe said \"hello\"", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_QuotedFieldSpanningLines_StaysOnOneLine()
    {
        // One line is one row for everything downstream, so a multi-line field must not create a row.
        var segment = Assert.Single(await ExtractAsync(_extractor, "Note,Author\n\"first\nsecond\",Ada\n"));

        Assert.Equal("Sheet: orders\nNote | Author\nfirst second | Ada", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_RaggedRows_AreKeptRatherThanRejected()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "A,B,C\n1,2\n3,4,5,6\n"));

        Assert.Equal("Sheet: orders\nA | B | C\n1 | 2\n3 | 4 | 5 | 6", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_WindowsLineEndings_AreNormalized()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "A,B\r\n1,2\r\n"));

        Assert.Equal("Sheet: orders\nA | B\n1 | 2", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_BlankLines_AreSkipped()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "A,B\n\n1,2\n\n"));

        Assert.Equal("Sheet: orders\nA | B\n1 | 2", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_Utf16WithByteOrderMark_IsDecodedCorrectly()
    {
        var content = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Name,City\nnaïve,café\n")).ToArray();

        var segment = Assert.Single(await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken));

        Assert.Equal("Sheet: orders\nName | City\nnaïve | café", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_Utf8WithByteOrderMark_DoesNotLeakTheMarkIntoTheFirstCell()
    {
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Name,City\nAda,Paris\n")).ToArray();

        var segment = Assert.Single(await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken));

        Assert.Equal("Sheet: orders\nName | City\nAda | Paris", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_WhitespaceOnlyFile_ReturnsNoSegments()
    {
        Assert.Empty(await ExtractAsync(_extractor, "   \n\n\t  \n"));
    }

    [Fact]
    public async Task ExtractAsync_File_ReportsProgressEndingAtCompletion()
    {
        var reports = new List<DocumentExtractionProgress>();

        await ExtractAsync(_extractor, "A,B\n1,2\n", progress: new CollectingProgress<DocumentExtractionProgress>(reports));

        Assert.Equal(2, reports.Count);
        Assert.Null(reports[0].Fraction);
        Assert.Equal(1d, reports[^1].Fraction);
        Assert.All(reports, report => Assert.False(string.IsNullOrWhiteSpace(report.Message)));
    }

    [Fact]
    public async Task ExtractAsync_FileAtTheRowCeiling_IsAccepted()
    {
        var segment = Assert.Single(await ExtractAsync(Create(maxRows: 3), "A,B\n1,2\n3,4\n"));

        Assert.Equal(4, segment.Text.Split('\n').Length);
    }

    [Fact]
    public async Task ExtractAsync_FileOneRowPastTheCeiling_IsRefusedNamingTheLimit()
    {
        var exception = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => ExtractAsync(Create(maxRows: 3), "A,B\n1,2\n3,4\n5,6\n"));

        Assert.Contains("3", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_FileOnePastTheColumnCeiling_IsRefusedNamingTheLimit()
    {
        var exception = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => ExtractAsync(Create(maxColumns: 3), "A,B,C,D\n1,2,3,4\n"));

        Assert.Contains("3", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_FileAtTheColumnCeiling_IsAccepted()
    {
        var segment = Assert.Single(await ExtractAsync(Create(maxColumns: 3), "A,B,C\n1,2,3\n"));

        Assert.Equal("Sheet: orders\nA | B | C\n1 | 2 | 3", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_DelimitersInsideQuotedFields_DoNotCountTowardTheColumnCeiling()
    {
        // The shape guard runs before the parser, so it has to honour quoting exactly as the parser does
        // or a legitimate prose column would be refused.
        var segment = Assert.Single(await ExtractAsync(Create(maxColumns: 2), "Note,Author\n\"a, b, c, d, e\",Ada\n"));

        Assert.Equal("Sheet: orders\nNote | Author\na, b, c, d, e | Ada", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_FilePastTheCharacterCeiling_IsRefused()
    {
        var exception = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => ExtractAsync(Create(maxCharacters: 32), "A,B\n" + string.Concat(Enumerable.Repeat("value,value\n", 20))));

        Assert.Contains("32", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_SingleColumnFile_ReadsOneCellPerRow()
    {
        // No candidate delimiter appears at all, so detection has to settle rather than pick noise.
        var segment = Assert.Single(await ExtractAsync(_extractor, "Alpha\nBravo\nCharlie\n"));

        Assert.Equal("Sheet: orders\nAlpha\nBravo\nCharlie", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_FileWhoseFirstLineHasNoDelimiter_StillResolvesTheRest()
    {
        // A title row above the header is common in exported reports.
        var segment = Assert.Single(await ExtractAsync(_extractor, "Quarterly report\nSKU;Region\nW-1;East\n"));

        Assert.Equal("Sheet: orders\nQuarterly report\nSKU | Region\nW-1 | East", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_StrayQuoteInAnUnquotedField_KeepsTheCellRatherThanRefusingTheFile()
    {
        // An inch mark or a quoted nickname is ordinary in an exported CSV; refusing the whole file for
        // one costs far more than reading that cell verbatim.
        var segment = Assert.Single(await ExtractAsync(_extractor, "Size,Owner\n12,5\" pipe\nSmall,Bob \"Bo\" Smith\n"));

        Assert.Contains("12 | 5\" pipe", segment.Text, StringComparison.Ordinal);
        Assert.Contains("Small | Bob \"Bo\" Smith", segment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_UnterminatedQuote_ReadsToTheEndRatherThanFailingTheUpload()
    {
        var segment = Assert.Single(await ExtractAsync(_extractor, "Name,City\n\"unterminated,East\nnext,West\n"));

        Assert.StartsWith("Sheet: orders\nName | City\n", segment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_CancelledToken_SurfacesCancellationRatherThanAValidationFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _extractor.ExtractAsync(File(Encoding.UTF8.GetBytes("A,B\n1,2\n")), progress: null, cancellation.Token));
    }

    [Fact]
    public async Task ExtractAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _extractor.ExtractAsync(null!, progress: null, TestContext.Current.CancellationToken));
    }
}
