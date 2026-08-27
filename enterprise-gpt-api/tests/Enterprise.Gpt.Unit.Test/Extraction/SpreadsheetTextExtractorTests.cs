using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;
using SheetSpec = Enterprise.Gpt.Unit.Test.TestInfrastructure.WorkbookBuilder.SheetSpec;

namespace Enterprise.Gpt.Unit.Test.Extraction;

public sealed class SpreadsheetTextExtractorTests
{
    private readonly SpreadsheetTextExtractor _extractor = Create();

    private static SpreadsheetTextExtractor Create(int maxRows = 20_000, int maxColumns = 200, int maxCharacters = 8_000_000)
    {
        var options = Options.Create(new SheetOptions
        {
            MaxRowsPerSheet = maxRows,
            MaxColumnsPerSheet = maxColumns,
            MaxCharactersPerUpload = maxCharacters
        });

        return new SpreadsheetTextExtractor(NullLogger<SpreadsheetTextExtractor>.Instance, options);
    }

    private static FileDto File(byte[] content, string fileName = "budget.xlsx")
    {
        return new FileDto
        {
            FileName = fileName,
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Length = content.Length,
            Content = content
        };
    }

    private static Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
        SpreadsheetTextExtractor extractor,
        byte[] content,
        IProgress<DocumentExtractionProgress>? progress = null)
    {
        return extractor.ExtractAsync(File(content), progress, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void SupportedExtensions_ContainsOnlyXlsx()
    {
        Assert.Equal([FileExtensions.Xlsx], _extractor.SupportedExtensions);
    }

    [Fact]
    public async Task ExtractAsync_MultiSheetWorkbook_ReturnsOneSegmentPerSheetInWorkbookOrder()
    {
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Regional Revenue", [["SKU", "Region"], ["W-1", "East"]]),
            new SheetSpec("Headcount", [["Team", "People"], ["Platform", 12d]]),
            new SheetSpec("Notes", [["Reviewed by finance"]])
        ]);

        var segments = await ExtractAsync(_extractor, content);

        Assert.Equal(3, segments.Count);
        Assert.Equal([1, 2, 3], segments.Select(s => s.SourceNumber));
        Assert.Equal("Sheet: Regional Revenue\nSKU | Region\nW-1 | East", segments[0].Text);
        Assert.Equal("Sheet: Headcount\nTeam | People\nPlatform | 12", segments[1].Text);
        Assert.Equal("Sheet: Notes\nReviewed by finance", segments[2].Text);
    }

    [Fact]
    public async Task ExtractAsync_SheetWithNoRows_ProducesNoSegmentForIt()
    {
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Populated", [["Value"]]),
            new SheetSpec("Empty", []),
            new SheetSpec("AlsoPopulated", [["Other"]])
        ]);

        var segments = await ExtractAsync(_extractor, content);

        Assert.Equal(2, segments.Count);
        Assert.Equal([1, 3], segments.Select(s => s.SourceNumber));
    }

    [Fact]
    public async Task ExtractAsync_EmptyRowBetweenPopulatedOnes_NeitherRepeatsNorCountsTowardTheCeiling()
    {
        // Pins the row start/end pairing: cells are read one at a time, so a self-closing row has to
        // clear the previous row's cells and flush nothing of its own.
        var content = WorkbookBuilder.Create([new SheetSpec("Gaps", [["a"], [], ["b"]])]);

        var segment = Assert.Single(await ExtractAsync(Create(maxRows: 2), content));

        Assert.Equal("Sheet: Gaps\na\nb", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_PackageInflatingPastItsBudget_IsRefusedBeforeThePackageIsOpened()
    {
        // A workbook is a deflate archive, so the accepted upload size bounds nothing on its own.
        var content = WorkbookBuilder.Create([new SheetSpec("Verbose", BuildRows(6_000))]);

        var exception = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => ExtractAsync(Create(maxCharacters: 1_024), content));

        Assert.Contains("decompressed", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_DateStyledCellHoldingText_FallsBackToTheCellsOwnValue()
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Dates", [[new WorkbookBuilder.RawAttributes("N/A", Reference: "A1", StyleIndex: "1")]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Dates\nN/A", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_HiddenSheet_IsStillExtracted()
    {
        // The workbook's lookup tables usually live on a hidden sheet, and dropping them would leave the
        // ingested picture of the workbook incomplete.
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Summary", [["Total", 10d]]),
            new SheetSpec("Lookup", [["Code", "Label"], ["EA", "East"]], Hidden: true)
        ]);

        var segments = await ExtractAsync(_extractor, content);

        Assert.Equal(2, segments.Count);
        Assert.Contains("Sheet: Lookup", segments[1].Text);
    }

    [Fact]
    public async Task ExtractAsync_SheetPrecededByAChartSheet_KeepsTheWorkbookOrdinal()
    {
        var content = WorkbookBuilder.CreateWithChartSheetBefore(new SheetSpec("Data", [["Value", 1d]]));

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal(2, segment.SourceNumber);
        Assert.Contains("Sheet: Data", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_RowWithMissingCells_KeepsLaterCellsInTheirOwnColumns()
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Gaps", [["A", "B", "C"], ["one", null, "three"], [null, null, "only"]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Gaps\nA | B | C\none |  | three\n |  | only", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_RowWithTrailingEmptyCells_DropsThem()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Trailing", [["A", "B", "C"], ["one", null, null]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Trailing\nA | B | C\none", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_TypedCells_RenderAsTheirDisplayValues()
    {
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Types",
            [
                [
                    new WorkbookBuilder.InlineText("Inline"),
                    true,
                    false,
                    new WorkbookBuilder.CellError("#DIV/0!"),
                    new WorkbookBuilder.Formula("SUM(B1:C1)", "42"),
                    new WorkbookBuilder.Formula("CONCAT(A1,B1)", "Joined", IsText: true),
                    1234.56d
                ]
            ])
        ]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Types\nInline | TRUE | FALSE | #DIV/0! | 42 | Joined | 1234.56", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_DateStyledCell_RendersAsADateRatherThanASerialNumber()
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Dates", [[new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Unspecified)]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Dates\n2026-03-14", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_DateStyledCellWithATimeOfDay_KeepsTheTime()
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Dates", [[new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Unspecified)]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Dates\n2026-03-14 09:30:00", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_CustomDateFormat_IsRecognizedAsADate()
    {
        // The format code is declared in the package rather than being one of the ids fixed by the spec.
        var serial = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Unspecified).ToOADate();
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Custom", [[new WorkbookBuilder.Styled(serial, WorkbookBuilder.CustomDateStyle)]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Custom\n2026-03-14", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_CustomNumberFormat_IsNotMistakenForADate()
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Custom", [[new WorkbookBuilder.Styled(45730d, WorkbookBuilder.CustomNumberStyle)]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Custom\n45730", segment.Text);
    }

    [Theory]
    [InlineData(1d, "1900-01-01")]
    [InlineData(59d, "1900-02-28")]
    [InlineData(61d, "1900-03-01")]
    public async Task ExtractAsync_DateBeforeMarch1900_MatchesExcelsOwnSerialNumbering(double serial, string expected)
    {
        // Excel's 1900 serials include a 29 February 1900 that never existed, so the two calendars only
        // agree from serial 61.
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Dates", [[new WorkbookBuilder.Styled(serial, WorkbookBuilder.BuiltInDateStyle)]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal($"Sheet: Dates\n{expected}", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_DateStyledCellHoldingOnlyATime_RendersTheTime()
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Times", [[new WorkbookBuilder.Styled(0.5d, WorkbookBuilder.BuiltInDateStyle)]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Times\n12:00:00", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_WorkbookOnThe1904DateSystem_ShiftsTheEpoch()
    {
        // Serial 0 is 1904-01-01 rather than 1899-12-30, a 1,462-day difference.
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Dates", [[new WorkbookBuilder.Styled(0d, WorkbookBuilder.BuiltInDateStyle)]])],
            use1904: true);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Dates\n1904-01-01", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_CellContainingALineBreak_StaysOnOneLine()
    {
        // One line is one row for everything downstream, so a cell's own newline must not create a row.
        var content = WorkbookBuilder.Create([new SheetSpec("Notes", [["first\nsecond", "after"]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Notes\nfirst second | after", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_SheetWithoutAName_FallsBackToItsOrdinal()
    {
        var content = WorkbookBuilder.Create([new SheetSpec(string.Empty, [["Value"]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.StartsWith("Sheet: Sheet1\n", segment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_Workbook_ReportsProgressForEverySheet()
    {
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("One", [["a"]]),
            new SheetSpec("Two", [["b"]]),
            new SheetSpec("Three", [["c"]])
        ]);
        var reports = new List<DocumentExtractionProgress>();

        await ExtractAsync(_extractor, content, new CollectingProgress<DocumentExtractionProgress>(reports));

        Assert.Equal(3, reports.Count);
        Assert.Equal(1d, reports[^1].Fraction);
        Assert.All(reports, report => Assert.False(string.IsNullOrWhiteSpace(report.Message)));
    }

    [Fact]
    public async Task ExtractAsync_SheetAtTheRowCeiling_IsAccepted()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Big", BuildRows(4))]);

        var segment = Assert.Single(await ExtractAsync(Create(maxRows: 4), content));

        Assert.Equal(5, segment.Text.Split('\n').Length);
    }

    [Fact]
    public async Task ExtractAsync_SheetOneRowPastTheCeiling_IsRefusedNamingTheLimit()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Big", BuildRows(5))]);

        var exception = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => ExtractAsync(Create(maxRows: 4), content));

        Assert.Contains("4", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_SheetAtTheColumnCeiling_IsAccepted()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Wide", [BuildColumns(3)])]);

        Assert.Single(await ExtractAsync(Create(maxColumns: 3), content));
    }

    [Fact]
    public async Task ExtractAsync_SheetOnePastTheColumnCeiling_IsRefusedNamingTheLimit()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Wide", [BuildColumns(4)])]);

        var exception = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => ExtractAsync(Create(maxColumns: 3), content));

        Assert.Contains("3", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_OnlyOneSheetPastACeiling_RefusesTheWholeWorkbook()
    {
        // Dropping just the offending sheet would leave a workbook that answers questions about itself
        // incorrectly, so the upload fails outright.
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Small", BuildRows(2)),
            new SheetSpec("Huge", BuildRows(9))
        ]);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(Create(maxRows: 4), content));
    }

    [Fact]
    public async Task ExtractAsync_ContentThatIsNotAWorkbook_ThrowsValidationException()
    {
        var content = "this is not a workbook"u8.ToArray();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(_extractor, content));
    }

    [Fact]
    public async Task ExtractAsync_PasswordProtectedWorkbook_ThrowsValidationException()
    {
        // Excel writes an encrypted workbook as an OLE2 compound file rather than a zip, so the package
        // never opens at all.
        var content = WorkbookBuilder.CreateEncryptedLikeWorkbook();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(_extractor, content));
    }

    [Fact]
    public async Task ExtractAsync_SheetIdPointingAtAMissingPart_ThrowsValidationException()
    {
        // The package opens cleanly and only fails when the dangling relationship is dereferenced.
        var content = WorkbookBuilder.CreateWithDanglingSheetRelationship();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(_extractor, content));
    }

    [Fact]
    public async Task ExtractAsync_WorksheetContainingMalformedXml_ThrowsValidationException()
    {
        // Part XML is parsed lazily, so this also throws long after Open() has returned.
        var content = WorkbookBuilder.CreateWithCorruptSheetXml();

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(_extractor, content));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("99999999999999999999")]
    public async Task ExtractAsync_CellWithAMalformedStyleIndex_ThrowsValidationException(string styleIndex)
    {
        // Typed attributes are stored as text and parsed on first access, so these throw long after the
        // package opens — where an unguarded parse would report an unexpected error rather than a 400.
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Data", [[new WorkbookBuilder.RawAttributes("1", Reference: "A1", StyleIndex: styleIndex)]])]);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(_extractor, content));
    }

    [Fact]
    public async Task ExtractAsync_CellWithAnUnknownDataType_ThrowsValidationException()
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Data", [[new WorkbookBuilder.RawAttributes("1", Reference: "A1", DataType: "zzz")]])]);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(_extractor, content));
    }

    [Theory]
    [InlineData("nope", "0")]
    [InlineData("164", "-9")]
    public async Task ExtractAsync_StylesheetWithAMalformedNumberFormatId_ThrowsValidationException(
        string numberFormatId,
        string cellFormatId)
    {
        var content = WorkbookBuilder.CreateWithMalformedNumberFormats(numberFormatId, cellFormatId);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(_extractor, content));
    }

    [Fact]
    public async Task ExtractAsync_CellsWithoutAReference_KeepTheirPositionWithinTheRow()
    {
        // Some generators omit the r attribute and rely on position, empty cells included.
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Positional",
            [
                [
                    new WorkbookBuilder.RawAttributes("first"),
                    new WorkbookBuilder.RawAttributes(string.Empty),
                    new WorkbookBuilder.RawAttributes("third")
                ]
            ])
        ]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Positional\nfirst |  | third", segment.Text);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9999")]
    [InlineData("notanumber")]
    public async Task ExtractAsync_SharedStringIndexOutsideTheTable_ReadsAsEmptyRatherThanThrowing(string index)
    {
        var content = WorkbookBuilder.Create(
            [new SheetSpec("Data", [["real", new WorkbookBuilder.RawSharedString(index)]])]);

        var segment = Assert.Single(await ExtractAsync(_extractor, content));

        Assert.Equal("Sheet: Data\nreal", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_SingleRowPastTheColumnCeiling_IsRefusedBeforeTheRowIsAssembled()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Wide", [BuildColumns(500)])]);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ExtractAsync(Create(maxColumns: 10), content));
    }

    [Fact]
    public async Task ExtractAsync_EmptyCellPastTheColumnCeiling_DoesNotRefuseTheWorkbook()
    {
        // Excel persists formatting-only cells far to the right of the data; refusing on those would name
        // a limit the user cannot act on.
        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Formatted",
            [
                [
                    new WorkbookBuilder.RawAttributes("value", Reference: "A1"),
                    new WorkbookBuilder.RawAttributes(string.Empty, Reference: "HZ1", StyleIndex: "3")
                ]
            ])
        ]);

        var segment = Assert.Single(await ExtractAsync(Create(maxColumns: 10), content));

        Assert.Equal("Sheet: Formatted\nvalue", segment.Text);
    }

    [Fact]
    public async Task ExtractAsync_WorkbookPastTheCharacterCeiling_IsRefused()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Verbose", BuildRows(20))]);

        var exception = await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => ExtractAsync(Create(maxCharacters: 64), content));

        Assert.Contains("64", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_CancelledToken_SurfacesCancellationRatherThanAValidationFailure()
    {
        var content = WorkbookBuilder.Create([new SheetSpec("Data", BuildRows(50))]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _extractor.ExtractAsync(File(content), progress: null, cancellation.Token));
    }

    [Fact]
    public async Task ExtractAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _extractor.ExtractAsync(null!, progress: null, TestContext.Current.CancellationToken));
    }

    private static List<object?[]> BuildRows(int count)
    {
        return [.. Enumerable.Range(1, count).Select(i => new object?[] { $"row {i}", (double)i })];
    }

    private static object?[] BuildColumns(int count)
    {
        return [.. Enumerable.Range(1, count).Select(object? (i) => $"column {i}")];
    }
}
