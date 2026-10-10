using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Globalization;
using System.Text;
using Xunit;
using SheetSpec = Enterprise.Gpt.Unit.Test.TestInfrastructure.WorkbookBuilder.SheetSpec;

namespace Enterprise.Gpt.Unit.Test.Extraction;

/// <summary>
/// Covers what row windows are for: surviving the unmodified chunker with their header attached, and
/// breaking on row boundaries when a window is too large to survive whole.
/// </summary>
public sealed class SheetChunkingTests
{
    private const string HeaderLine = "SKU | Region | Revenue | Quarter";

    [Fact]
    public async Task Chunk_SheetSpanningManyWindows_KeepsTheHeaderInAlmostEveryChunk()
    {
        var segments = await ExtractAsync(2_000);

        var chunks = TestChunker.Default.Chunk(segments, TestContext.Current.CancellationToken);
        var withHeader = chunks.Count(chunk => chunk.Text.Contains(HeaderLine, StringComparison.Ordinal));

        Assert.True(chunks.Count > 20, $"Expected a corpus of many chunks, got {chunks.Count}.");
        Assert.True(
            withHeader >= chunks.Count * 0.95,
            $"Only {withHeader} of {chunks.Count} chunks carried the header line.");
    }

    [Fact]
    public async Task Chunk_WindowLargerThanTheChunkBudget_BreaksOnRowBoundaries()
    {
        // The extractor sizes windows against the chunker it was given; a smaller budget here forces the
        // degraded path, where the chunker's sentence boundary — which breaks on a newline — is the only
        // thing keeping a row's own cells together. Overlap is off because its token-level tail is a
        // separate, accepted behaviour that seeds a chunk with part of a row by design.
        var segments = await ExtractAsync(200);
        var rows = Rows(segments);

        var chunks = TestChunker.Create(maxTokens: 128, overlapTokens: 0)
            .Chunk(segments, TestContext.Current.CancellationToken);

        var lines = chunks.SelectMany(chunk => chunk.Text.Split('\n')).ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.True(
            line.StartsWith("Sheet: ", StringComparison.Ordinal) || rows.Contains(line),
            $"'{line}' is not a whole row."));
    }

    /// <summary>
    /// The same shaping through the other extractor, so a mixed corpus's spreadsheet half has one
    /// header-repetition rate rather than two.
    /// </summary>
    [Fact]
    public async Task Chunk_CsvSpanningManyWindows_KeepsTheHeaderInAlmostEveryChunk()
    {
        var extractor = new CsvTextExtractor(
            NullLogger<CsvTextExtractor>.Instance, Options.Create(new SheetOptions()), TestChunker.Default);

        var content = Encoding.UTF8.GetBytes(BuildCsv(2_000));
        var file = new FileDto
        {
            FileName = "orders.csv",
            ContentType = "text/csv",
            Length = content.Length,
            Content = content
        };

        var segments = await extractor.ExtractAsync(file, progress: null, TestContext.Current.CancellationToken);
        var chunks = TestChunker.Default.Chunk(segments, TestContext.Current.CancellationToken);
        var withHeader = chunks.Count(chunk => chunk.Text.Contains(HeaderLine, StringComparison.Ordinal));

        Assert.True(chunks.Count > 20, $"Expected a corpus of many chunks, got {chunks.Count}.");
        Assert.True(
            withHeader >= chunks.Count * 0.95,
            $"Only {withHeader} of {chunks.Count} chunks carried the header line.");
    }

    /// <summary>
    /// Every window of every sheet opens with its own sheet name, so a workbook's chunks stay
    /// attributable to the sheet they came from however many sheets it holds.
    /// </summary>
    [Fact]
    public async Task Chunk_MultiSheetWorkbook_KeepsEachSheetsOwnHeaderInItsOwnChunks()
    {
        var extractor = new SpreadsheetTextExtractor(
            NullLogger<SpreadsheetTextExtractor>.Instance, Options.Create(new SheetOptions()), TestChunker.Default);

        var content = WorkbookBuilder.Create(
        [
            new SheetSpec("Regional Revenue", DataRows(400)),
            new SheetSpec("Headcount", DataRows(400))
        ]);

        var file = new FileDto
        {
            FileName = "budget.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Length = content.Length,
            Content = content
        };

        var segments = await extractor.ExtractAsync(file, progress: null, TestContext.Current.CancellationToken);
        var chunks = TestChunker.Default.Chunk(segments, TestContext.Current.CancellationToken);

        string[] sheetNames = ["Regional Revenue", "Headcount"];

        foreach (var sheetName in sheetNames)
        {
            var forSheet = chunks
                .Where(chunk => chunk.Text.Contains($"Sheet: {sheetName}", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(forSheet);
            Assert.True(
                forSheet.Count(chunk => chunk.Text.Contains(HeaderLine, StringComparison.Ordinal)) >= forSheet.Count * 0.95,
                $"Sheet '{sheetName}' lost its header in more than one chunk in twenty.");
        }
    }

    private static string BuildCsv(int dataRows)
    {
        var builder = new StringBuilder("SKU,Region,Revenue,Quarter\n");

        for (var i = 1; i <= dataRows; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"SKU-{i:0000},East,{i * 137},Q3\n");
        }

        return builder.ToString();
    }

    private static List<object?[]> DataRows(int dataRows)
    {
        List<object?[]> rows = [["SKU", "Region", "Revenue", "Quarter"]];
        rows.AddRange(Enumerable.Range(1, dataRows).Select(i => new object?[]
        {
            string.Create(CultureInfo.InvariantCulture, $"SKU-{i:0000}"),
            "East",
            (double)(i * 137),
            "Q3"
        }));

        return rows;
    }

    private static HashSet<string> Rows(IReadOnlyList<DocumentSegmentDto> segments)
    {
        return
        [
            .. segments
                .SelectMany(segment => segment.Text.Split('\n'))
                .Where(line => !line.StartsWith("Sheet: ", StringComparison.Ordinal))
        ];
    }

    private static Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(int dataRows)
    {
        var options = Options.Create(new SheetOptions());
        var extractor = new SpreadsheetTextExtractor(
            NullLogger<SpreadsheetTextExtractor>.Instance, options, TestChunker.Default);

        List<object?[]> rows = [["SKU", "Region", "Revenue", "Quarter"]];
        rows.AddRange(Enumerable.Range(1, dataRows).Select(i => new object?[]
        {
            string.Create(CultureInfo.InvariantCulture, $"SKU-{i:0000}"),
            "East",
            (double)(i * 137),
            "Q3"
        }));

        var content = WorkbookBuilder.Create([new SheetSpec("Regional Revenue", rows)]);
        var file = new FileDto
        {
            FileName = "budget.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Length = content.Length,
            Content = content
        };

        return extractor.ExtractAsync(file, progress: null, TestContext.Current.CancellationToken);
    }
}
