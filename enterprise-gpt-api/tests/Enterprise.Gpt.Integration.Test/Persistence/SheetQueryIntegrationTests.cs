using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Persistence;

/// <summary>
/// Covers the deterministic spreadsheet lookup against a real SQL Server 2025, which is the only place
/// it can run: the unit-test fixture derives its own store type for the native <c>json</c> cells column
/// and has no <c>JSON_VALUE</c> of SQL Server's shape, so every statement the tool builds is exercised
/// here or nowhere.
/// </summary>
/// <remarks>
/// The seeded sheet is small and its figures are round, so every expected value in the benchmark below
/// was computed by hand from the table in <c>Benchmark()</c> rather than from a second implementation of
/// the thing under test.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SheetQueryIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() =>
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    #region The fixed benchmark

    [Fact]
    public async Task QueryAsync_Sum_TotalsEveryReadableValue()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "sum", column: "Revenue");

        Assert.Equal(6750d, result.Value);
        Assert.Equal(10, result.MatchedRowCount);
    }

    [Fact]
    public async Task QueryAsync_Avg_DividesByTheRowsThatActuallyCarriedAValue()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "avg", column: "Revenue");

        // 6750 over the nine rows with a revenue, not the ten that matched.
        Assert.Equal(750d, result.Value);
    }

    [Fact]
    public async Task QueryAsync_Min_ReturnsTheSmallestValue()
    {
        var scope = await SeedBenchmarkAsync();

        Assert.Equal(25d, (await QueryAsync(scope, "min", column: "Revenue")).Value);
    }

    [Fact]
    public async Task QueryAsync_Max_ReturnsTheLargestValue()
    {
        var scope = await SeedBenchmarkAsync();

        Assert.Equal(2000d, (await QueryAsync(scope, "max", column: "Revenue")).Value);
    }

    [Fact]
    public async Task QueryAsync_CountWithNoColumn_CountsEveryRow()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "count");

        Assert.Equal(10, result.Value);
        Assert.Equal(10, result.MatchedRowCount);
    }

    [Fact]
    public async Task QueryAsync_CountWithAColumn_CountsTheRowsThatHaveOne()
    {
        var scope = await SeedBenchmarkAsync();

        // One row leaves Revenue empty, and ingestion stores an empty cell as absent.
        Assert.Equal(9, (await QueryAsync(scope, "count", column: "Revenue")).Value);
    }

    [Fact]
    public async Task QueryAsync_Sum_ReportsTheRowsItCouldNotRead()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "sum", column: "Revenue");

        // A total that quietly skipped a row would read as a complete one.
        Assert.Equal(1, result.SkippedValues);
    }

    [Fact]
    public async Task QueryAsync_SumGroupedByAColumn_ReturnsOneTotalPerDistinctValue()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "sum", column: "Revenue", groupBy: "Region");

        Assert.NotNull(result.Groups);
        Assert.Equal(["East", "North", "South", "West"], result.Groups.Select(g => g.Group));
        Assert.Equal([2700d, 100d, 2500d, 1450d], result.Groups.Select(g => g.Value));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task QueryAsync_CountGroupedByAColumn_CountsTheRowsInEachGroup()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "count", groupBy: "Region");

        Assert.NotNull(result.Groups);
        Assert.Equal([3, 2, 2, 3], result.Groups.Select(g => g.RowCount));
    }

    [Fact]
    public async Task QueryAsync_SumWithAFilter_TotalsOnlyTheMatchingRows()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "sum", column: "Revenue", filters: [Filter("Quarter", "eq", "Q3")]);

        Assert.Equal(2475.5d, result.Value);
        Assert.Equal(5, result.MatchedRowCount);
    }

    [Fact]
    public async Task QueryAsync_GroupedSumWithAFilter_AppliesBoth()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(
            scope, "sum", column: "Revenue", groupBy: "Region", filters: [Filter("Quarter", "eq", "Q3")]);

        Assert.NotNull(result.Groups);
        Assert.Equal(["East", "North", "South", "West"], result.Groups.Select(g => g.Group));
        Assert.Equal([1500.5d, 25d, 500d, 450d], result.Groups.Select(g => g.Value));
    }

    [Fact]
    public async Task QueryAsync_BooleanColumn_ComparesAsTextWhateverCaseTheModelUsed()
    {
        var scope = await SeedBenchmarkAsync();

        // Cells hold TRUE and FALSE; the comparison collation is what makes "true" find them.
        Assert.Equal(7, (await QueryAsync(scope, "count", filters: [Filter("Shipped", "eq", "true")])).Value);
    }

    [Fact]
    public async Task QueryAsync_FilterOnANumericColumn_ComparesNumericallyRatherThanAsText()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", filters: [Filter("Revenue", "gte", "1000")]);

        // As text, "300.5" would sort above "1000"; only a numeric read gets these four.
        Assert.NotNull(result.Rows);
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(["W-1", "W-4", "W-7", "W-9"], result.Rows.Select(r => r.Cells["SKU"]));
    }

    [Fact]
    public async Task QueryAsync_FilterOnADateColumn_ComparesChronologically()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", filters: [Filter("OrderDate", "lt", "2026-04-01")]);

        Assert.NotNull(result.Rows);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public async Task QueryAsync_NotEqual_KeepsTheRowsWithNoValueInThatColumn()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", filters: [Filter("Region", "neq", "East")]);

        Assert.NotNull(result.Rows);
        Assert.Equal(7, result.Rows.Count);
    }

    [Fact]
    public async Task QueryAsync_Contains_MatchesASubstringOfTheCellsText()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", filters: [Filter("SKU", "contains", "W-1")]);

        Assert.NotNull(result.Rows);
        Assert.Equal(["W-1", "W-10"], result.Rows.Select(r => r.Cells["SKU"]));
    }

    [Fact]
    public async Task QueryAsync_MinOnADateColumn_ReturnsTheEarliestDate()
    {
        var scope = await SeedBenchmarkAsync();

        Assert.Equal("2026-01-15", (await QueryAsync(scope, "min", column: "OrderDate")).Value);
    }

    [Fact]
    public async Task QueryAsync_MaxOnATextColumn_ReturnsTheLastValueInOrder()
    {
        var scope = await SeedBenchmarkAsync();

        Assert.Equal("Q4", (await QueryAsync(scope, "max", column: "Quarter")).Value);
    }

    [Fact]
    public async Task QueryAsync_AvgWithAFilter_AveragesOnlyTheMatchingRows()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "avg", column: "Revenue", filters: [Filter("Region", "eq", "East")]);

        // 1200 + 300.5 + 1,199.50 over three rows — the last written with a group separator, which the
        // column's own inference accepts and the numeric read has to strip.
        Assert.Equal(900d, result.Value);
    }

    [Fact]
    public async Task QueryAsync_SeveralFilters_RequiresEveryOneOfThem()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(
            scope, "filter", filters: [Filter("Quarter", "eq", "Q3"), Filter("Region", "eq", "East")]);

        Assert.NotNull(result.Rows);
        Assert.Equal(["W-1", "W-2"], result.Rows.Select(r => r.Cells["SKU"]));
    }

    #endregion

    #region Shape, caps and empty results

    [Fact]
    public async Task QueryAsync_FilterResult_ReturnsEachRowsCellsAsFieldsRatherThanText()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", filters: [Filter("SKU", "eq", "W-1")]);

        var row = Assert.Single(result.Rows!);
        Assert.Equal(0, row.RowIndex);
        Assert.Equal("East", row.Cells["Region"]);
        Assert.Equal("1200", row.Cells["Revenue"]);
    }

    [Fact]
    public async Task QueryAsync_RowWithAnEmptyCell_OmitsItRatherThanReturningItBlank()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", filters: [Filter("SKU", "eq", "W-10")]);

        var row = Assert.Single(result.Rows!);
        Assert.False(row.Cells.ContainsKey("Revenue"));
    }

    [Fact]
    public async Task QueryAsync_NoMatchingRows_ReturnsAnEmptyResultRatherThanAnError()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", filters: [Filter("Region", "eq", "Antarctica")]);

        Assert.Empty(result.Rows!);
        Assert.Equal(0, result.MatchedRowCount);
        Assert.False(result.Truncated);
        Assert.Contains("No rows matched", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_SumOverNoMatchingRows_ReturnsNothingRatherThanZero()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(
            scope, "sum", column: "Revenue", filters: [Filter("Region", "eq", "Antarctica")]);

        // A zero would be indistinguishable from a real total of zero.
        Assert.Null(result.Value);
        Assert.Equal(0, result.MatchedRowCount);
    }

    [Fact]
    public async Task QueryAsync_MoreRowsThanTheRowCap_ReportsTheTrimAndTheFullCount()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "filter", options: new SheetQueryOptions { MaxResultRows = 3 });

        Assert.Equal(3, result.Rows!.Count);
        Assert.Equal(10, result.MatchedRowCount);
        Assert.True(result.Truncated);

        // A trimmed result explains itself in Note, which is not the same fact as a refusal.
        Assert.False(result.IsRefusal);
    }

    [Fact]
    public async Task QueryAsync_RowsWiderThanTheCharacterBudget_StopsAtTheBudget()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(
            scope, "filter", options: new SheetQueryOptions { MaxResultCharacters = 1_000 });

        // The row cap alone bounds how many rows come back, not how wide they are.
        Assert.InRange(result.Rows!.Count, 1, 9);
        Assert.Equal(10, result.MatchedRowCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task QueryAsync_ARowWiderThanTheWholeBudget_IsStillReturned()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddConversationDocumentAsync(
            conversationId, "notes.xlsx", [], cancellationToken: TestContext.Current.CancellationToken);

        // A single cell can hold tens of thousands of characters, so one row alone can outweigh the
        // smallest budget an operator is allowed to configure.
        var wide = new string('x', 2_000);
        await _fixture.AddConversationDocumentSheetAsync(
            documentId,
            new SeedSheet(
                1,
                "Notes",
                [new SeedSheetColumn("Note", SheetColumnType.Text)],
                [
                    new Dictionary<string, string> { ["Note"] = wide },
                    new Dictionary<string, string> { ["Note"] = wide }
                ]),
            cancellationToken: TestContext.Current.CancellationToken);

        var scope = new DocumentRetrievalScope(
            conversationId,
            null,
            [new RetrievableDocument(documentId, DocumentSource.Conversation, "notes.xlsx")]);

        var result = await QueryAsync(
            scope, "filter", options: new SheetQueryOptions { MaxResultCharacters = 1_000 });

        // Returning nothing at all would read as "no rows matched", which is a different answer.
        Assert.Single(result.Rows!);
        Assert.Equal(2, result.MatchedRowCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task QueryAsync_MoreGroupsThanTheGroupCap_ReportsTheTrim()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(
            scope, "sum", column: "Revenue", groupBy: "Region", options: new SheetQueryOptions { MaxGroups = 2 });

        Assert.Equal(2, result.Groups!.Count);
        Assert.True(result.Truncated);
        Assert.Contains("first 2 groups", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_ContainsWithALikeWildcard_MatchesItLiterally()
    {
        var scope = await SeedBenchmarkAsync();

        // Unescaped, "%" would match every row.
        var result = await QueryAsync(scope, "filter", filters: [Filter("SKU", "contains", "%")]);

        Assert.Empty(result.Rows!);
    }

    [Fact]
    public async Task QueryAsync_SumOverANonNumericColumn_ReadsWhatItCanAndSaysWhatItSkipped()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "sum", column: "Quarter");

        // A column with one stray cell among its first rows infers as text; refusing to total it would
        // leave a genuinely numeric column permanently unusable.
        Assert.Null(result.Value);
        Assert.Equal(10, result.SkippedValues);
        Assert.Contains("not uniformly numeric", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_CountOfNoMatchingRows_ReportsZeroWithoutCallingItAFailure()
    {
        var scope = await SeedBenchmarkAsync();

        var result = await QueryAsync(scope, "count", filters: [Filter("Region", "eq", "Antarctica")]);

        // Zero is the answer to a count, and a note saying otherwise is what the model would relay
        // instead of the figure.
        Assert.Equal(0, result.Value);
        Assert.Null(result.Note);
    }

    [Theory]
    [InlineData("09:00:00")]
    [InlineData("9:00:00")]
    [InlineData("09:00")]
    [InlineData("9:00 AM")]
    public async Task QueryAsync_TimeOnlyColumn_ComparesAgainstTheSameAnchorTheDatabaseUses(string wanted)
    {
        var scope = await SeedTimesAsync();

        // A cell holding only a time converts to datetime2 as 1900-01-01 plus that time; parsing the
        // model's value against today's date instead would make every comparison miss. The cells all
        // carry one format, but the value beside them is whatever the model wrote.
        var result = await QueryAsync(scope, "filter", filters: [Filter("Start", "gt", wanted)]);

        Assert.NotNull(result.Rows);
        Assert.Equal(["B", "C"], result.Rows.Select(r => r.Cells["Shift"]));
    }

    [Fact]
    public async Task QueryAsync_MinOfATimeOnlyColumn_ReportsTheTimeWithoutAnInventedDate()
    {
        var scope = await SeedTimesAsync();

        Assert.Equal("06:30:00", (await QueryAsync(scope, "min", column: "Start")).Value);
    }

    [Fact]
    public async Task QueryAsync_GroupsAndFiltersFoldAccentsTheSameWay()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddConversationDocumentAsync(
            conversationId, "regions.xlsx", [], cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.AddConversationDocumentSheetAsync(
            documentId,
            new SeedSheet(
                1,
                "Regions",
                [new SeedSheetColumn("Region", SheetColumnType.Text), new SeedSheetColumn("Amount", SheetColumnType.Number)],
                [
                    new Dictionary<string, string> { ["Region"] = "Café", ["Amount"] = "10" },
                    new Dictionary<string, string> { ["Region"] = "cafe", ["Amount"] = "5" }
                ]),
            cancellationToken: TestContext.Current.CancellationToken);

        var scope = new DocumentRetrievalScope(
            conversationId,
            null,
            [new RetrievableDocument(documentId, DocumentSource.Conversation, "regions.xlsx")]);

        var grouped = await QueryAsync(scope, "sum", column: "Amount", groupBy: "Region");
        var filtered = await QueryAsync(scope, "sum", column: "Amount", filters: [Filter("Region", "eq", "cafe")]);

        // One value to a filter and two groups to a breakdown would be two answers to the same question.
        Assert.Equal(15d, Assert.Single(grouped.Groups!).Value);
        Assert.Equal(15d, filtered.Value);
    }

    #endregion

    #region Scope and soft delete

    [Fact]
    public async Task QueryAsync_SheetOfAnotherConversation_IsNotReachableEvenWhenNamedInScope()
    {
        var scope = await SeedBenchmarkAsync();
        var otherConversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // The scope carries the other conversation's own id while still naming the seeded document, so
        // the statement's own ownership predicates are the only thing standing between them.
        var forged = new DocumentRetrievalScope(otherConversationId, null, scope.Documents);

        var result = await QueryAsync(forged, "count");

        Assert.Equal(0, result.Value);
        Assert.Equal(0, result.MatchedRowCount);
    }

    [Fact]
    public async Task QueryAsync_DeactivatedRows_AreNotCounted()
    {
        var scope = await SeedBenchmarkAsync(rowsDeactivated: true);

        Assert.Equal(0, (await QueryAsync(scope, "count")).Value);
    }

    [Fact]
    public async Task QueryAsync_DeactivatedSheet_CannotBeResolvedAtAll()
    {
        var scope = await SeedBenchmarkAsync(sheetDeactivated: true);

        var result = await QueryAsync(scope, "count");

        Assert.Contains("No spreadsheet", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_ConversationInAProject_ReachesTheProjectsOwnSheets()
    {
        var projectId = await _fixture.AddProjectAsync(
            name: "Finance", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddProjectDocumentAsync(
            projectId, "budget.xlsx", [], cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.AddProjectDocumentSheetAsync(
            documentId, Benchmark(), TestContext.Current.CancellationToken);

        var scope = new DocumentRetrievalScope(
            conversationId,
            projectId,
            [new RetrievableDocument(documentId, DocumentSource.Project, "budget.xlsx")]);

        Assert.Equal(6750d, (await QueryAsync(scope, "sum", column: "Revenue")).Value);
    }

    [Fact]
    public async Task QueryAsync_StandaloneConversation_NeverReachesAProjectsSheets()
    {
        var projectId = await _fixture.AddProjectAsync(
            name: "Finance", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddProjectDocumentAsync(
            projectId, "budget.xlsx", [], cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.AddProjectDocumentSheetAsync(
            documentId, Benchmark(), TestContext.Current.CancellationToken);

        // A scope with no project is what a standalone conversation resolves to, and the project half of
        // every statement is gated on that being null.
        var scope = new DocumentRetrievalScope(
            conversationId, null, [new RetrievableDocument(documentId, DocumentSource.Project, "budget.xlsx")]);

        Assert.Equal(0, (await QueryAsync(scope, "count")).Value);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The benchmark sheet. Every expected value in this file was computed by hand from this table.
    /// </summary>
    private static SeedSheet Benchmark() => new(
        SheetIndex: 1,
        Name: "Regional Revenue",
        Columns:
        [
            new SeedSheetColumn("SKU", SheetColumnType.Text),
            new SeedSheetColumn("Region", SheetColumnType.Text),
            new SeedSheetColumn("Revenue", SheetColumnType.Number),
            new SeedSheetColumn("Quarter", SheetColumnType.Text),
            new SeedSheetColumn("Shipped", SheetColumnType.Boolean),
            new SeedSheetColumn("OrderDate", SheetColumnType.Date)
        ],
        Rows:
        [
            Row("W-1", "East", "1200", "Q3", "TRUE", "2026-01-15"),
            Row("W-2", "East", "300.5", "Q3", "FALSE", "2026-02-20"),
            Row("W-3", "West", "450", "Q3", "TRUE", "2026-03-10"),
            Row("W-4", "West", "1000", "Q4", "TRUE", "2026-04-01"),
            Row("W-5", "North", "25", "Q3", "FALSE", "2026-05-05"),
            Row("W-6", "North", "75", "Q4", "TRUE", "2026-06-30"),
            Row("W-7", "South", "2000", "Q4", "TRUE", "2026-07-04"),
            Row("W-8", "South", "500", "Q3", "FALSE", "2026-08-12"),
            Row("W-9", "East", "1,199.50", "Q4", "TRUE", "2026-09-09"),
            Row("W-10", "West", "", "Q4", "TRUE", "2026-10-31")
        ]);

    private static Dictionary<string, string> Row(
        string sku, string region, string revenue, string quarter, string shipped, string orderDate) => new()
        {
            ["SKU"] = sku,
            ["Region"] = region,
            ["Revenue"] = revenue,
            ["Quarter"] = quarter,
            ["Shipped"] = shipped,
            ["OrderDate"] = orderDate
        };

    private static SheetFilter Filter(string column, string comparator, string value) =>
        new() { Column = column, Comparator = comparator, Value = value };

    private async Task<DocumentRetrievalScope> SeedBenchmarkAsync(
        bool sheetDeactivated = false, bool rowsDeactivated = false)
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddConversationDocumentAsync(
            conversationId, "budget.xlsx", [], cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.AddConversationDocumentSheetAsync(
            documentId, Benchmark(), sheetDeactivated, rowsDeactivated, TestContext.Current.CancellationToken);

        return new DocumentRetrievalScope(
            conversationId,
            null,
            [new RetrievableDocument(documentId, DocumentSource.Conversation, "budget.xlsx")]);
    }

    /// <summary>
    /// A sheet whose date column holds only times, which is what the extractor's own format list makes a
    /// shift or duration column infer as.
    /// </summary>
    private async Task<DocumentRetrievalScope> SeedTimesAsync()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddConversationDocumentAsync(
            conversationId, "shifts.xlsx", [], cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.AddConversationDocumentSheetAsync(
            documentId,
            new SeedSheet(
                1,
                "Shifts",
                [new SeedSheetColumn("Shift", SheetColumnType.Text), new SeedSheetColumn("Start", SheetColumnType.Date)],
                [
                    new Dictionary<string, string> { ["Shift"] = "A", ["Start"] = "06:30:00" },
                    new Dictionary<string, string> { ["Shift"] = "B", ["Start"] = "14:00:00" },
                    new Dictionary<string, string> { ["Shift"] = "C", ["Start"] = "22:15:00" }
                ]),
            cancellationToken: TestContext.Current.CancellationToken);

        return new DocumentRetrievalScope(
            conversationId,
            null,
            [new RetrievableDocument(documentId, DocumentSource.Conversation, "shifts.xlsx")]);
    }

    private async Task<SheetQueryResult> QueryAsync(
        DocumentRetrievalScope scope,
        string operation,
        string? sheetName = null,
        string? column = null,
        string? groupBy = null,
        IReadOnlyList<SheetFilter>? filters = null,
        SheetQueryOptions? options = null)
    {
        using var container = _fixture.Factory.Services.CreateScope();

        var service = new SheetQueryService(
            NullLogger<SheetQueryService>.Instance,
            new FakeSheetQueryOptionsProvider(options),
            container.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>());

        return await service.QueryAsync(
            scope, operation, sheetName, column, groupBy, filters, TestContext.Current.CancellationToken);
    }

    #endregion
}
