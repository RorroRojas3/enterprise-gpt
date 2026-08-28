using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Extraction;

/// <summary>
/// Covers the shaping both spreadsheet formats share, at the boundaries their own fixtures cannot
/// reach cheaply — a token-bound window, a sheet at the column ceiling, an oversized row.
/// </summary>
public sealed class SheetAssemblerTests
{
    [Fact]
    public void Assemble_LongRows_ClosesAWindowOnTheTokenBudgetRatherThanTheRowLimit()
    {
        List<string[]> rows = [["Description", "Region"]];
        rows.AddRange(Enumerable.Range(1, 40).Select(i => new[]
        {
            string.Create(CultureInfo.InvariantCulture, $"A deliberately long description of the {i} widget line, its packaging and its warranty terms"),
            "East"
        }));

        var windows = Windows(SheetAssembler.Assemble("Catalog", 1, rows, new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken));

        // Every window is under the budget while the row limit is nowhere near reached.
        Assert.True(windows.Count > 1);
        Assert.All(windows, window =>
        {
            Assert.True(TestChunker.Default.CountTokens(window) <= 384);
            Assert.True(window.Split('\n').Length - 2 < new RowWindowOptions().MaxRows);
        });
    }

    [Fact]
    public void Assemble_RowLargerThanTheWholeBudget_StillGetsAWindowOfItsOwn()
    {
        var giant = string.Join(" ", Enumerable.Repeat("overflowing", 600));
        List<string[]> rows = [["Note"], [giant], ["short"]];

        var windows = Windows(SheetAssembler.Assemble("Notes", 1, rows, new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken));

        Assert.Equal(2, windows.Count);
        Assert.Contains(giant, windows[0], StringComparison.Ordinal);
        Assert.EndsWith("short", windows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Assemble_SheetTooWideToRepeatItsHeader_DropsTheHeaderRatherThanEmittingOneRowPerWindow()
    {
        // A header taking most of the budget would otherwise be copied onto every row, turning a
        // 20,000-row sheet into 20,000 mostly-header segments.
        var header = Enumerable.Range(1, 60)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"Measurement {i}"))
            .ToArray();

        List<string[]> rows = [header];
        rows.AddRange(Enumerable.Range(1, 200).Select(_ => Enumerable.Repeat("v", 60).ToArray()));

        var windows = Windows(SheetAssembler.Assemble("Wide", 1, rows, new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken));

        Assert.True(windows.Count < 200, $"Expected rows to share windows, got {windows.Count} windows for 200 rows.");
        Assert.All(windows, window => Assert.StartsWith("Sheet: Wide\nv | v", window, StringComparison.Ordinal));
    }

    [Fact]
    public void Assemble_HeadingMixingWordsAndYears_IsStillReadAsAHeader()
    {
        // The shape of every budget export: one label column and a year per measure. Reading it as
        // data would name the columns by position and fold the headings into the sheet's own totals.
        var sheet = SheetAssembler.Assemble("Budget", 1, [["Region", "2023", "2024"], ["East", "100", "120"]],
            new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken).Structure;

        Assert.Equal(["Region", "2023", "2024"], sheet.Columns.Select(c => c.ColumnName));
        Assert.Equal(1, sheet.RowCount);
    }

    [Fact]
    public void Assemble_HeaderlessRowOfMixedText_StaysDataRatherThanBecomingTheHeadings()
    {
        // The other half of the heading heuristic: taking this row for headings would drop it from
        // every total and name the columns after its own values.
        var sheet = SheetAssembler.Assemble("Dump", 1, [["W-1", "East", "100"], ["W-2", "West", "200"]],
            new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken).Structure;

        Assert.Equal(["Column1", "Column2", "Column3"], sheet.Columns.Select(c => c.ColumnName));
        Assert.Equal(2, sheet.RowCount);
        Assert.Equal("""{"Column1":"W-1","Column2":"East","Column3":"100"}""", sheet.Rows[0].Cells);
    }

    [Fact]
    public void Assemble_SheetNameAloneTooLongForAWindow_DropsThePrefixRatherThanTheRows()
    {
        // A name is capped in characters, not tokens, so a script that tokenizes several to the
        // character can still crowd every row out of its own window.
        var name = string.Concat(Enumerable.Repeat("計測対象", 64));

        var windows = Windows(SheetAssembler.Assemble(name, 1, [["1"], ["2"], ["3"]],
            new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken));

        Assert.Equal("1\n2\n3", Assert.Single(windows));
    }

    [Fact]
    public void Assemble_CellsExpandingPastTheirBudget_AreRefusedNamingTheLimit()
    {
        List<string[]> rows = [["Description"]];
        rows.AddRange(Enumerable.Range(1, 100).Select(i => new[] { string.Create(CultureInfo.InvariantCulture, $"value {i}") }));

        var exception = Assert.Throws<FluentValidation.ValidationException>(() => SheetAssembler.Assemble(
            "Big", 1, rows, new RowWindowOptions(), TestChunker.Default, 256, TestContext.Current.CancellationToken));

        Assert.Contains("256", Assert.Single(exception.Errors).ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemble_Sheet_ReportsWhatItsCellsCostToStore()
    {
        var assembly = SheetAssembler.Assemble("Small", 1, [["A"], ["one"], ["two"]],
            new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken);

        Assert.Equal(assembly.Structure.Rows.Sum(r => r.Cells.Length), assembly.CellCharacters);
    }

    [Fact]
    public void Assemble_SheetWithFarMoreColumnsThanTheCardCanHold_SaysHowManyItLeftOut()
    {
        var header = Enumerable.Range(1, 200)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"Measurement column number {i}"))
            .ToArray();
        var data = Enumerable.Range(1, 200).Select(_ => "value").ToArray();

        var card = SheetAssembler.Assemble("Wide", 1, [header, data], new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken)
            .Segments[0].Text;

        Assert.Contains("more columns", card, StringComparison.Ordinal);
        Assert.True(TestChunker.Default.CountTokens(card) <= 384 + 32);
    }

    [Fact]
    public void Assemble_HeadingLongerThanTheColumnNameLimit_IsTruncatedToFitTheColumn()
    {
        var heading = new string('h', 400);

        var sheet = SheetAssembler.Assemble("Long", 1, [[heading], ["value"]], new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken)
            .Structure;

        Assert.Equal(256, Assert.Single(sheet.Columns).ColumnName.Length);
    }

    [Theory]
    [InlineData("TRUE", SheetColumnType.Boolean)]
    [InlineData("false", SheetColumnType.Boolean)]
    [InlineData("1234.56", SheetColumnType.Number)]
    [InlineData("2026-03-14", SheetColumnType.Date)]
    [InlineData("2026-03-14 09:30:00", SheetColumnType.Date)]
    [InlineData("12:00:00", SheetColumnType.Date)]
    [InlineData("W-1", SheetColumnType.Text)]
    [InlineData("14/03/2026", SheetColumnType.Text)]
    public void Assemble_ColumnOfOneKind_InfersThatKind(string value, SheetColumnType expected)
    {
        var sheet = SheetAssembler.Assemble("Types", 1, [["Value"], [value], [value]], new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken)
            .Structure;

        Assert.Equal(expected, Assert.Single(sheet.Columns).InferredType);
    }

    [Fact]
    public void Assemble_ColumnMixingKinds_FallsBackToText()
    {
        var sheet = SheetAssembler.Assemble("Mixed", 1, [["Value"], ["1200"], ["n/a"]], new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken)
            .Structure;

        Assert.Equal(SheetColumnType.Text, Assert.Single(sheet.Columns).InferredType);
    }

    [Fact]
    public void Assemble_ColumnWithNoValuesInTheSample_IsText()
    {
        var sheet = SheetAssembler.Assemble("Sparse", 1, [["A", "B"], ["one", ""], ["two", ""]], new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken)
            .Structure;

        Assert.Equal(SheetColumnType.Text, sheet.Columns[1].InferredType);
    }

    [Fact]
    public void Assemble_BlankHeadingCell_IsNamedByItsPosition()
    {
        var sheet = SheetAssembler.Assemble("Gaps", 1, [["A", "", "C"], ["1", "2", "3"]], new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken)
            .Structure;

        Assert.Equal(["A", "Column2", "C"], sheet.Columns.Select(c => c.ColumnName));
    }

    [Fact]
    public void Assemble_CellHoldingALoneSurrogate_StillProducesValidJson()
    {
        // Excel will store one, and a JSON writer that refused it would fail the whole upload.
        var broken = "before\ud800after";

        var sheet = SheetAssembler.Assemble("Broken", 1, [["Value"], [broken]], new RowWindowOptions(), TestChunker.Default, int.MaxValue, TestContext.Current.CancellationToken)
            .Structure;

        var cells = JsonDocument.Parse(Assert.Single(sheet.Rows).Cells);

        Assert.Equal("before�after", cells.RootElement.GetProperty("Value").GetString());
    }

    private static List<string> Windows(SheetAssembly assembly)
    {
        return
        [
            .. assembly.Segments
                .Skip(1)
                .Select(segment => segment.Text)
        ];
    }
}
