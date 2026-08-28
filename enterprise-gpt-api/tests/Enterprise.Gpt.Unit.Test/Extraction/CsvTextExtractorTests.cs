using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Globalization;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Extraction;

public sealed class CsvTextExtractorTests
{
    private readonly CsvTextExtractor _extractor = Create();

    private static CsvTextExtractor Create(
        int maxRows = 20_000,
        int maxColumns = 200,
        int maxCharacters = 8_000_000,
        RowWindowOptions? rowWindow = null)
    {
        var options = Options.Create(new SheetOptions
        {
            MaxRowsPerSheet = maxRows,
            MaxColumnsPerSheet = maxColumns,
            MaxCharactersPerUpload = maxCharacters,
            RowWindow = rowWindow ?? new RowWindowOptions()
        });

        return new CsvTextExtractor(NullLogger<CsvTextExtractor>.Instance, options, TestChunker.Default);
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

    private static Task<SheetExtractionResult> ExtractSheetsAsync(CsvTextExtractor extractor, string content)
    {
        return extractor.ExtractSheetsAsync(File(Encoding.UTF8.GetBytes(content)), progress: null, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A schema card is the segment whose second line describes the columns; a sheet's other segments
    /// are its row windows.
    /// </summary>
    private static bool IsSchemaCard(DocumentSegmentDto segment)
    {
        var lines = segment.Text.Split('\n');

        return lines.Length > 1 && lines[1].StartsWith("Columns: ", StringComparison.Ordinal);
    }

    private static List<DocumentSegmentDto> Windows(IReadOnlyList<DocumentSegmentDto> segments)
    {
        return [.. segments.Where(segment => !IsSchemaCard(segment))];
    }

    private static string WindowText(IReadOnlyList<DocumentSegmentDto> segments)
    {
        return Assert.Single(Windows(segments)).Text;
    }

    [Fact]
    public void SupportedExtensions_ContainsOnlyCsv()
    {
        Assert.Equal([FileExtensions.Csv], _extractor.SupportedExtensions);
    }

    [Fact]
    public async Task ExtractAsync_CommaDelimitedFile_ReturnsSegmentsNamedAfterTheFile()
    {
        var segments = await ExtractAsync(_extractor, "SKU,Region\nW-1,East\nW-2,West\n", "q3-orders.csv");

        Assert.All(segments, segment => Assert.Equal(1, segment.SourceNumber));
        Assert.Equal("Sheet: q3-orders\nSKU | Region\nW-1 | East\nW-2 | West", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_File_EmitsOneSchemaCardAheadOfItsRowWindows()
    {
        var segments = await ExtractAsync(_extractor, "SKU,Revenue\nW-1,1200\nW-2,800\n");

        Assert.Equal(2, segments.Count);
        Assert.True(IsSchemaCard(segments[0]));
        Assert.Equal(
            "Sheet: orders\nColumns: SKU (text), Revenue (number)\nRows: 2\nSample rows:\nSKU | Revenue\nW-1 | 1200\nW-2 | 800",
            segments[0].Text);
    }

    [Fact]
    public async Task ExtractAsync_FileSpanningSeveralWindows_RepeatsTheHeaderInEveryOne()
    {
        var builder = new StringBuilder("SKU,Region\n");

        for (var i = 1; i <= 200; i++)
        {
            builder.Append(DataRow(i));
        }

        var windows = Windows(await ExtractAsync(_extractor, builder.ToString()));

        Assert.True(windows.Count > 1);
        Assert.All(windows, window => Assert.StartsWith("Sheet: orders\nSKU | Region\n", window.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_ShortRows_AreCappedByTheWindowRowLimitRatherThanTheTokenBudget()
    {
        var builder = new StringBuilder("SKU,Region\n");

        for (var i = 1; i <= 25; i++)
        {
            builder.Append(DataRow(i));
        }

        var windows = Windows(await ExtractAsync(Create(rowWindow: new RowWindowOptions { MaxRows = 10 }), builder.ToString()));

        Assert.Equal(3, windows.Count);
        Assert.Equal([12, 12, 7], windows.Select(w => w.Text.Split('\n').Length));
    }

    [Fact]
    public async Task ExtractSheetsAsync_File_IsModelledAsASingleSheetWorkbook()
    {
        var sheet = Assert.Single((await ExtractSheetsAsync(_extractor, "SKU,Revenue\nW-1,1200\nW-2,800\n")).Sheets);

        Assert.Equal(1, sheet.SheetIndex);
        Assert.Equal("orders", sheet.SheetName);
        Assert.Equal(2, sheet.RowCount);
        Assert.Equal(2, sheet.ColumnCount);
        Assert.Equal(["SKU", "Revenue"], sheet.Columns.Select(c => c.ColumnName));
        Assert.Equal([SheetColumnType.Text, SheetColumnType.Number], sheet.Columns.Select(c => c.InferredType));
        Assert.Equal("""{"SKU":"W-1","Revenue":"1200"}""", sheet.Rows[0].Cells);
    }

    [Fact]
    public async Task ExtractSheetsAsync_RaggedRow_KeepsItsExtraCellsUnderGeneratedColumnNames()
    {
        var sheet = Assert.Single((await ExtractSheetsAsync(_extractor, "A,B,C\n1,2\n3,4,5,6\n")).Sheets);

        Assert.Equal(4, sheet.ColumnCount);
        Assert.Equal(["A", "B", "C", "Column4"], sheet.Columns.Select(c => c.ColumnName));
        Assert.Equal("""{"A":"1","B":"2"}""", sheet.Rows[0].Cells);
        Assert.Equal("""{"A":"3","B":"4","C":"5","Column4":"6"}""", sheet.Rows[1].Cells);
    }

    [Fact]
    public async Task ExtractSheetsAsync_WhitespaceOnlyFile_ReportsNoSheets()
    {
        Assert.Empty((await ExtractSheetsAsync(_extractor, "   \n\n\t  \n")).Sheets);
    }

    [Fact]
    public async Task ExtractAsync_SemicolonDelimitedFile_ResolvesItsColumns()
    {
        var segments = await ExtractAsync(_extractor, "SKU;Region;Revenue\nW-1;East;1200\nW-2;West;980\n");

        Assert.Equal("Sheet: orders\nSKU | Region | Revenue\nW-1 | East | 1200\nW-2 | West | 980", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_TabDelimitedFile_ResolvesItsColumns()
    {
        var segments = await ExtractAsync(_extractor, "SKU\tRegion\tRevenue\nW-1\tEast\t1200\nW-2\tWest\t980\n");

        Assert.Equal("Sheet: orders\nSKU | Region | Revenue\nW-1 | East | 1200\nW-2 | West | 980", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_SemicolonDelimitedFileWithDecimalCommas_StillResolvesTheSemicolon()
    {
        // A comma appears on every line as a decimal separator, which is exactly the case a detector that
        // prefers the culture's list separator gets wrong.
        var segments = await ExtractAsync(_extractor, "Item;Price;Tax\nWidget;1,50;0,30\nGadget;2,75;0,55\n");

        Assert.Equal("Sheet: orders\nItem | Price | Tax\nWidget | 1,50 | 0,30\nGadget | 2,75 | 0,55", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_QuotedFieldContainingTheDelimiter_KeepsItInOneCell()
    {
        var segments = await ExtractAsync(_extractor, "Name,Address\n\"Ltd, Acme\",\"1 High St\"\n");

        Assert.Equal("Sheet: orders\nName | Address\nLtd, Acme | 1 High St", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_QuotedFieldContainingEscapedQuotes_UnescapesThem()
    {
        var segments = await ExtractAsync(_extractor, "Name\n\"He said \"\"hello\"\"\"\n");

        Assert.Equal("Sheet: orders\nName\nHe said \"hello\"", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_QuotedFieldSpanningLines_StaysOnOneLine()
    {
        // One line is one row for everything downstream, so a multi-line field must not create a row.
        var segments = await ExtractAsync(_extractor, "Note,Author\n\"first\nsecond\",Ada\n");

        Assert.Equal("Sheet: orders\nNote | Author\nfirst second | Ada", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_RaggedRows_AreKeptRatherThanRejected()
    {
        var segments = await ExtractAsync(_extractor, "A,B,C\n1,2\n3,4,5,6\n");

        Assert.Equal("Sheet: orders\nA | B | C\n1 | 2\n3 | 4 | 5 | 6", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_WindowsLineEndings_AreNormalized()
    {
        var segments = await ExtractAsync(_extractor, "A,B\r\n1,2\r\n");

        Assert.Equal("Sheet: orders\nA | B\n1 | 2", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_BlankLines_AreSkipped()
    {
        var segments = await ExtractAsync(_extractor, "A,B\n\n1,2\n\n");

        Assert.Equal("Sheet: orders\nA | B\n1 | 2", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_Utf16WithByteOrderMark_IsDecodedCorrectly()
    {
        var content = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Name,City\nnaïve,café\n")).ToArray();

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal("Sheet: orders\nName | City\nnaïve | café", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_Utf8WithByteOrderMark_DoesNotLeakTheMarkIntoTheFirstCell()
    {
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Name,City\nAda,Paris\n")).ToArray();

        var segments = await _extractor.ExtractAsync(File(content), progress: null, TestContext.Current.CancellationToken);

        Assert.Equal("Sheet: orders\nName | City\nAda | Paris", WindowText(segments));
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
        var segments = await ExtractAsync(Create(maxRows: 3), "A,B\n1,2\n3,4\n");

        Assert.Equal(4, WindowText(segments).Split('\n').Length);
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
        var segments = await ExtractAsync(Create(maxColumns: 3), "A,B,C\n1,2,3\n");

        Assert.Equal("Sheet: orders\nA | B | C\n1 | 2 | 3", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_DelimitersInsideQuotedFields_DoNotCountTowardTheColumnCeiling()
    {
        // The shape guard runs before the parser, so it has to honour quoting exactly as the parser does
        // or a legitimate prose column would be refused.
        var segments = await ExtractAsync(Create(maxColumns: 2), "Note,Author\n\"a, b, c, d, e\",Ada\n");

        Assert.Equal("Sheet: orders\nNote | Author\na, b, c, d, e | Ada", WindowText(segments));
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
        var segments = await ExtractAsync(_extractor, "Alpha\nBravo\nCharlie\n");

        Assert.Equal("Sheet: orders\nAlpha\nBravo\nCharlie", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_FileWhoseFirstLineHasNoDelimiter_StillResolvesTheRest()
    {
        // A title row above the header is common in exported reports.
        var segments = await ExtractAsync(_extractor, "Quarterly report\nSKU;Region\nW-1;East\n");

        Assert.Equal("Sheet: orders\nQuarterly report\nSKU | Region\nW-1 | East", WindowText(segments));
    }

    [Fact]
    public async Task ExtractAsync_StrayQuoteInAnUnquotedField_KeepsTheCellRatherThanRefusingTheFile()
    {
        // An inch mark or a quoted nickname is ordinary in an exported CSV; refusing the whole file for
        // one costs far more than reading that cell verbatim.
        var text = WindowText(await ExtractAsync(_extractor, "Size,Owner\n12,5\" pipe\nSmall,Bob \"Bo\" Smith\n"));

        Assert.Contains("12 | 5\" pipe", text, StringComparison.Ordinal);
        Assert.Contains("Small | Bob \"Bo\" Smith", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_UnterminatedQuote_ReadsToTheEndRatherThanFailingTheUpload()
    {
        var segments = await ExtractAsync(_extractor, "Name,City\n\"unterminated,East\nnext,West\n");

        Assert.StartsWith("Sheet: orders\nName | City\n", WindowText(segments), StringComparison.Ordinal);
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

    [Fact]
    public async Task ExtractSheetsAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _extractor.ExtractSheetsAsync(null!, progress: null, TestContext.Current.CancellationToken));
    }

    private static string DataRow(int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"SKU-{index:0000},East\n");
    }
}
