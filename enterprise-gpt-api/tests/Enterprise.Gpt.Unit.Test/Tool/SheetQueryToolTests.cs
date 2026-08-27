using Enterprise.Gpt.Service.Tool;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers the function the model actually sees: its name and schema, that the turn's own scope is what
/// reaches the service rather than anything from the arguments, and that neither a database message nor
/// a stalled query reaches the model as anything but a sentence it can act on.
/// </summary>
public sealed class SheetQueryToolTests
{
    private const int TimeoutSeconds = 30;

    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid BudgetId = Guid.NewGuid();
    private static readonly Guid HandbookId = Guid.NewGuid();

    private readonly ISheetQueryService _sheetQueryService = Substitute.For<ISheetQueryService>();

    [Fact]
    public void Create_NamesTheToolSoUsageIsNotAttributedToAnMcpServer()
    {
        var function = CreateFunction();

        // Tool-call usage is credited to an MCP server by longest "{server}_" prefix match, so the first
        // segment of this name must not be a plausible server name.
        Assert.Equal("sheet_query", function.Name);
        Assert.Equal(SheetQueryTool.ToolName, function.Name);
        Assert.False(string.IsNullOrWhiteSpace(function.Description));
    }

    [Fact]
    public void Create_DeclaresOperationAsTheOnlyRequiredArgument()
    {
        var schema = CreateFunction().JsonSchema;
        var properties = schema.GetProperty("properties");

        foreach (var name in new[] { "operation", "sheetName", "column", "groupBy", "filters" })
        {
            Assert.True(properties.TryGetProperty(name, out _), name);
        }

        // Everything but the operation is optional: omitting the sheet name is how the model asks what
        // exists, and the rest depend on the operation.
        var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal("operation", Assert.Single(required));
    }

    [Fact]
    public void Create_DoesNotExposeTheCancellationTokenToTheModel()
    {
        var properties = CreateFunction().JsonSchema.GetProperty("properties");

        Assert.False(properties.TryGetProperty("cancellationToken", out _));
    }

    [Fact]
    public async Task InvokeAsync_PassesTheTurnsOwnScopeRatherThanAnythingFromTheArguments()
    {
        _sheetQueryService
            .QueryAsync(
                Arg.Any<DocumentRetrievalScope>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<SheetFilter>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new SheetQueryResult { Operation = "sum", Value = 12d });

        var function = CreateFunction();

        await function.InvokeAsync(
            new AIFunctionArguments { ["operation"] = "sum", ["column"] = "Revenue" },
            TestContext.Current.CancellationToken);

        await _sheetQueryService.Received(1).QueryAsync(
            Arg.Is<DocumentRetrievalScope>(s => s.ConversationId == ConversationId),
            "sum",
            null,
            "Revenue",
            null,
            Arg.Any<IReadOnlyList<SheetFilter>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_Filters_ReachTheServiceAsSuppliedRatherThanAsText()
    {
        _sheetQueryService
            .QueryAsync(
                Arg.Any<DocumentRetrievalScope>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<SheetFilter>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new SheetQueryResult { Operation = "filter", Rows = [] });

        var function = CreateFunction();

        await function.InvokeAsync(
            new AIFunctionArguments
            {
                ["operation"] = "filter",
                ["filters"] = new[] { new { column = "Quarter", comparator = "eq", value = "Q3" } }
            },
            TestContext.Current.CancellationToken);

        await _sheetQueryService.Received(1).QueryAsync(
            Arg.Any<DocumentRetrievalScope>(),
            "filter",
            null,
            null,
            null,
            Arg.Is<IReadOnlyList<SheetFilter>?>(f =>
                f != null && f.Count == 1 && f[0].Column == "Quarter" && f[0].Comparator == "eq" && f[0].Value == "Q3"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_QueryFails_ReplacesTheMessageWithSomethingSafeToShowTheModel()
    {
        _sheetQueryService
            .QueryAsync(
                Arg.Any<DocumentRetrievalScope>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<SheetFilter>?>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Login failed for user 'sa'. Server=prod-sql-01;Password=hunter2"));

        var function = CreateFunction();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["operation"] = "count" }, TestContext.Current.CancellationToken).AsTask());

        // Function invocation is configured with detailed errors, so whatever is thrown here is handed to
        // the model as-is. The original message goes to the log and nowhere else.
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-sql-01", exception.Message, StringComparison.Ordinal);
        Assert.Contains("spreadsheet", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_ItsOwnDeadlineElapses_TellsTheModelRatherThanFailingTheTurn()
    {
        _sheetQueryService
            .QueryAsync(
                Arg.Any<DocumentRetrievalScope>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<SheetFilter>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());

                return new SheetQueryResult { Operation = "count" };
            });

        var function = SheetQueryTool.Create(Scope(), _sheetQueryService, 1, NullLogger.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["operation"] = "count" }, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("too long", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_TurnCancelled_StaysCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _sheetQueryService
            .QueryAsync(
                Arg.Any<DocumentRetrievalScope>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<SheetFilter>?>(),
                Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var function = CreateFunction();

        // Turning this into a tool error would have the model work around a turn already being torn down.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["operation"] = "count" }, cts.Token).AsTask());
    }

    [Fact]
    public void CountSpreadsheets_CountsOnlyTheFormatsThatStoreSheets()
    {
        Assert.Equal(1, SheetQueryTool.CountSpreadsheets(Scope()));
    }

    [Fact]
    public void Create_InvalidArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SheetQueryTool.Create(null!, _sheetQueryService, TimeoutSeconds, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(
            () => SheetQueryTool.Create(Scope(), null!, TimeoutSeconds, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(
            () => SheetQueryTool.Create(Scope(), _sheetQueryService, TimeoutSeconds, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SheetQueryTool.Create(Scope(), _sheetQueryService, 0, NullLogger.Instance));
    }

    private AIFunction CreateFunction() =>
        SheetQueryTool.Create(Scope(), _sheetQueryService, TimeoutSeconds, NullLogger.Instance);

    private static DocumentRetrievalScope Scope() => new(
        ConversationId,
        null,
        [
            new RetrievableDocument(BudgetId, DocumentSource.Conversation, "budget.xlsx"),
            new RetrievableDocument(HandbookId, DocumentSource.Conversation, "handbook.pdf")
        ]);
}
