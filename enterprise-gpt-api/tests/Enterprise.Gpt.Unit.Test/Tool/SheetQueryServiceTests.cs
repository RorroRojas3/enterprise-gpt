using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tool;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers everything the sheet query decides before it builds a statement: which sheets a turn can
/// reach, how a named sheet or column resolves, and what it refuses rather than guessing.
/// </summary>
/// <remarks>
/// The statements themselves are absent by necessity — SQLite derives its own store type for the
/// <c>json</c> cells column and has no <c>JSON_VALUE</c> of SQL Server's shape — so every test here stops
/// at a refusal. Execution lives in <c>Enterprise.Gpt.Integration.Test</c> against SQL Server 2025.
/// </remarks>
public sealed class SheetQueryServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly SheetQueryOptions _options = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task QueryAsync_UnknownOperation_RefusesAndNamesTheOnesThatExist()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(scope, "median", column: "Revenue");

        Assert.Contains("not an operation", result.Note, StringComparison.Ordinal);
        Assert.Contains("sum", result.Note, StringComparison.Ordinal);
        Assert.Equal("median", result.Operation);
    }

    [Fact]
    public async Task QueryAsync_NoSpreadsheetInScope_SaysSoRatherThanFailing()
    {
        var conversationId = Guid.NewGuid();
        var scope = new DocumentRetrievalScope(
            conversationId, null, [new RetrievableDocument(Guid.NewGuid(), DocumentSource.Conversation, "handbook.pdf")]);

        var result = await QueryAsync(scope, "count");

        Assert.Contains("No spreadsheet", result.Note, StringComparison.Ordinal);
        Assert.Null(result.AvailableSheets);
    }

    [Fact]
    public async Task QueryAsync_SheetNameOmittedWithOneSheet_UsesItWithoutAsking()
    {
        var scope = await SeedBudgetAsync();

        // Resolution succeeded if the refusal that comes back is about the column rather than the sheet.
        var result = await QueryAsync(scope, "sum", column: "Nope");

        Assert.Contains("no column named 'Nope'", result.Note, StringComparison.Ordinal);
        Assert.Equal("Regional Revenue", result.SheetName);
    }

    [Fact]
    public async Task QueryAsync_SheetNameOmittedWithSeveralSheets_RefusesAndDescribesEachOne()
    {
        var scope = await SeedBudgetAsync(secondSheet: true);

        var result = await QueryAsync(scope, "sum", column: "Revenue");

        Assert.Contains("More than one sheet", result.Note, StringComparison.Ordinal);
        Assert.NotNull(result.AvailableSheets);
        Assert.Equal(2, result.AvailableSheets.Count);

        // One refusal is meant to be enough to ask the right question next, so it carries each sheet's
        // shape rather than only its name.
        Assert.Contains("budget.xlsx — Regional Revenue (2 rows)", result.AvailableSheets[0], StringComparison.Ordinal);
        Assert.Contains("Revenue (numbers)", result.AvailableSheets[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_UnknownSheetName_RefusesAndNamesTheSheetsThatExist()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(scope, "count", sheetName: "Payroll");

        Assert.Contains("No sheet is named 'Payroll'", result.Note, StringComparison.Ordinal);
        Assert.NotNull(result.AvailableSheets);
    }

    [Fact]
    public async Task QueryAsync_AmbiguousSheetName_RefusesRatherThanPickingOne()
    {
        var scope = await SeedBudgetAsync(secondSheet: true);

        // Naming the workbook rather than a sheet inside it matches every sheet it holds.
        var result = await QueryAsync(scope, "count", sheetName: "budget.xlsx");

        Assert.Contains("matches more than one sheet", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_SheetNamedByItsDocumentQualifiedLabel_Resolves()
    {
        var scope = await SeedBudgetAsync(secondSheet: true);

        var result = await QueryAsync(scope, "sum", sheetName: "budget.xlsx — Regional Revenue", column: "Nope");

        Assert.Equal("Regional Revenue", result.SheetName);
        Assert.Contains("no column named 'Nope'", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_UnknownColumn_RefusesAndNamesTheColumnsWithTheirTypes()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(scope, "sum", column: "Profit");

        Assert.NotNull(result.AvailableColumns);
        Assert.Contains("SKU (text)", result.AvailableColumns);
        Assert.Contains("Revenue (numbers)", result.AvailableColumns);
    }

    [Fact]
    public async Task QueryAsync_AggregateWithoutAColumn_RefusesRatherThanGuessingOne()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(scope, "sum");

        Assert.Contains("needs a column", result.Note, StringComparison.Ordinal);
        Assert.NotNull(result.AvailableColumns);
    }

    [Fact]
    public async Task QueryAsync_UnknownGroupByColumn_RefusesTheSameWayAValueColumnDoes()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(scope, "sum", column: "Revenue", groupBy: "Territory");

        Assert.Contains("no column named 'Territory'", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_MoreFiltersThanAllowed_RefusesNamingTheLimit()
    {
        var scope = await SeedBudgetAsync();
        _options.MaxFilters = 2;

        var result = await QueryAsync(
            scope,
            "filter",
            filters:
            [
                new SheetFilter { Column = "Region", Comparator = "eq", Value = "East" },
                new SheetFilter { Column = "Region", Comparator = "eq", Value = "West" },
                new SheetFilter { Column = "Region", Comparator = "eq", Value = "North" }
            ]);

        // Trimming the list instead would return rows the model believes it excluded.
        Assert.Contains("Only 2 filters", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_UnknownComparator_RefusesAndNamesTheOnesThatExist()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(
            scope, "filter", filters: [new SheetFilter { Column = "Region", Comparator = "like", Value = "East" }]);

        Assert.Contains("not a comparator", result.Note, StringComparison.Ordinal);
        Assert.Contains("contains", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_FilterValueThatCannotBeReadAsItsColumnsType_RefusesRatherThanMatchingNothing()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(
            scope, "filter", filters: [new SheetFilter { Column = "Revenue", Comparator = "gt", Value = "lots" }]);

        // An empty result would read to the model as "no rows match", which is a worse failure than
        // being told the value was the wrong kind.
        Assert.Contains("cannot be compared", result.Note, StringComparison.Ordinal);
        Assert.Contains("numbers", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_FilterWithNoColumn_RefusesRatherThanIgnoringIt()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(
            scope, "filter", filters: [new SheetFilter { Comparator = "eq", Value = "East" }]);

        Assert.Contains("Every filter needs a column", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_SheetOfAnotherConversation_CannotBeReached()
    {
        await SeedBudgetAsync();

        // The scope names a document id that exists, but the turn is a different conversation, so
        // nothing about it is in scope to begin with.
        var scope = new DocumentRetrievalScope(Guid.NewGuid(), null, []);

        var result = await QueryAsync(scope, "count", sheetName: "Regional Revenue");

        Assert.Contains("No spreadsheet", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_DeactivatedSheet_IsNotResolvable()
    {
        var scope = await SeedBudgetAsync(deactivateSheet: true);

        var result = await QueryAsync(scope, "count", sheetName: "Regional Revenue");

        Assert.Contains("No spreadsheet", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_DeactivatedColumn_IsNotOfferedOrResolvable()
    {
        var scope = await SeedBudgetAsync(deactivateRevenueColumn: true);

        var result = await QueryAsync(scope, "sum", column: "Revenue");

        Assert.Contains("no column named 'Revenue'", result.Note, StringComparison.Ordinal);
        Assert.NotNull(result.AvailableColumns);
        Assert.DoesNotContain(result.AvailableColumns, c => c.StartsWith("Revenue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryAsync_ColumnNameThatCannotBeAJsonPath_RefusesRatherThanBuildingOne()
    {
        var scope = await SeedBudgetAsync(quotedColumnName: true);

        var result = await QueryAsync(scope, "count", column: "Size (\"in\")");

        Assert.Contains("cannot be used in a lookup", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_FilterValueLongerThanTheBoundParameter_RefusesRatherThanTruncatingIt()
    {
        var scope = await SeedBudgetAsync();

        var result = await QueryAsync(
            scope,
            "filter",
            filters: [new SheetFilter { Column = "Region", Comparator = "contains", Value = new string('x', 2_500) }]);

        // Truncation would quietly turn a substring match into a prefix one by dropping the wildcard.
        Assert.Contains("longer than", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_SheetWithManyColumns_NamesSomeAndSaysHowManyItLeftOut()
    {
        var scope = await SeedBudgetAsync(columnCount: 40);

        var result = await QueryAsync(scope, "sum", column: "Nope");

        // A refusal is the one reply no configured cap bounds, and a wide sheet's column list is the
        // thing that grows.
        Assert.NotNull(result.AvailableColumns);
        Assert.Equal(25, result.AvailableColumns.Count);
        Assert.EndsWith("more columns", result.AvailableColumns[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HasQueryableSheetsAsync_DocumentWithNoIngestedSheet_IsFalse()
    {
        var scope = await SeedBudgetAsync(withoutSheets: true);

        Assert.False(await CreateService().HasQueryableSheetsAsync(scope, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasQueryableSheetsAsync_DocumentWithAnIngestedSheet_IsTrue()
    {
        var scope = await SeedBudgetAsync();

        Assert.True(await CreateService().HasQueryableSheetsAsync(scope, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasQueryableSheetsAsync_DeactivatedSheet_IsFalse()
    {
        var scope = await SeedBudgetAsync(deactivateSheet: true);

        Assert.False(await CreateService().HasQueryableSheetsAsync(scope, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Region", "$.\"Region\"")]
    [InlineData("Total (USD)", "$.\"Total (USD)\"")]
    [InlineData("Régión", "$.\"Régión\"")]
    public void TryBuildJsonPath_OrdinaryName_QuotesItWithoutEscaping(string name, string expected)
    {
        Assert.True(SheetQueryService.TryBuildJsonPath(name, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\tb")]
    public void TryBuildJsonPath_NameThatWouldNeedEscaping_IsRefused(string name)
    {
        Assert.False(SheetQueryService.TryBuildJsonPath(name, out var path));
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void MatchSheetByName_PrefersTheNarrowestRungThatMatches()
    {
        IReadOnlyList<ResolvedSheet> sheets =
        [
            Sheet("Revenue"),
            Sheet("Revenue Detail"),
            Sheet("Costs")
        ];

        // Exact wins over the prefix match that would otherwise make the name ambiguous.
        Assert.Equal("Revenue", Assert.Single(MatchNames(sheets, "Revenue")));
        Assert.Equal(2, SheetQueryService.MatchSheetByName(sheets, "Reven").Count);
        Assert.Equal("Revenue Detail", Assert.Single(MatchNames(sheets, "Detail")));
        Assert.Empty(SheetQueryService.MatchSheetByName(sheets, "Payroll"));
    }

    private SheetQueryService CreateService() =>
        new(NullLogger<SheetQueryService>.Instance, Options.Create(_options), _fixture.Context);

    private Task<SheetQueryResult> QueryAsync(
        DocumentRetrievalScope scope,
        string operation,
        string? sheetName = null,
        string? column = null,
        string? groupBy = null,
        IReadOnlyList<SheetFilter>? filters = null) =>
        CreateService().QueryAsync(
            scope, operation, sheetName, column, groupBy, filters, TestContext.Current.CancellationToken);

    private static IEnumerable<string> MatchNames(IReadOnlyList<ResolvedSheet> sheets, string wanted) =>
        SheetQueryService.MatchSheetByName(sheets, wanted).Select(s => s.SheetName);

    private static ResolvedSheet Sheet(string name) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        DocumentSource.Conversation,
        "budget.xlsx",
        name,
        $"budget.xlsx — {name}",
        2,
        [new ResolvedSheetColumn("Revenue", SheetColumnType.Number)]);

    private async Task<DocumentRetrievalScope> SeedBudgetAsync(
        bool secondSheet = false,
        bool deactivateSheet = false,
        bool deactivateRevenueColumn = false,
        bool quotedColumnName = false,
        bool withoutSheets = false,
        int columnCount = 0)
    {
        var date = DateTimeOffset.UtcNow;

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date
        };

        var document = new ConversationDocument
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            ConversationId = conversation.Id,
            Name = "budget.xlsx",
            Extension = ".xlsx",
            MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Size = 1024,
            Path = "path/to/budget.xlsx",
            DateCreated = date,
            DateModified = date,
            Sheets =
            [
                new ConversationDocumentSheet
                {
                    Id = Guid.NewGuid(),
                    SheetIndex = 1,
                    SheetName = "Regional Revenue",
                    RowCount = 2,
                    ColumnCount = 3,
                    DateCreated = date,
                    DateModified = date,
                    DateDeactivated = deactivateSheet ? date : null,
                    Columns =
                    [
                        Column(0, "SKU", SheetColumnType.Text, date),
                        Column(1, "Region", SheetColumnType.Text, date),
                        Column(
                            2,
                            quotedColumnName ? "Size (\"in\")" : "Revenue",
                            quotedColumnName ? SheetColumnType.Text : SheetColumnType.Number,
                            date,
                            deactivateRevenueColumn ? date : null)
                    ],
                    Rows =
                    [
                        Row(0, """{"SKU":"W-1","Region":"East","Revenue":"1200"}""", date),
                        Row(1, """{"SKU":"W-2","Region":"West","Revenue":"340.5"}""", date)
                    ]
                }
            ]
        };

        if (withoutSheets)
        {
            document.Sheets.Clear();
        }

        if (columnCount > 0)
        {
            var sheet = document.Sheets[0];
            sheet.Columns.Clear();

            for (var i = 0; i < columnCount; i++)
            {
                sheet.Columns.Add(Column(
                    i, string.Create(CultureInfo.InvariantCulture, $"Column{i}"), SheetColumnType.Text, date));
            }
        }

        if (secondSheet)
        {
            document.Sheets.Add(new ConversationDocumentSheet
            {
                Id = Guid.NewGuid(),
                SheetIndex = 2,
                SheetName = "Revenue Forecast",
                RowCount = 1,
                ColumnCount = 1,
                DateCreated = date,
                DateModified = date,
                Columns = [Column(0, "Revenue", SheetColumnType.Number, date)],
                Rows = [Row(0, """{"Revenue":"99"}""", date)]
            });
        }

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        ctx.ConversationDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new DocumentRetrievalScope(
            conversation.Id,
            null,
            [new RetrievableDocument(document.Id, DocumentSource.Conversation, document.Name)]);
    }

    private static ConversationDocumentSheetColumn Column(
        int index, string name, SheetColumnType type, DateTimeOffset date, DateTimeOffset? deactivated = null) => new()
        {
            ColumnIndex = index,
            ColumnName = name,
            InferredType = type,
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

    private static ConversationDocumentSheetRow Row(int index, string cells, DateTimeOffset date) => new()
    {
        RowIndex = index,
        Cells = cells,
        DateCreated = date,
        DateModified = date
    };
}
