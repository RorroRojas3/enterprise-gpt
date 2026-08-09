using Enterprise.Gpt.Service.Tool;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers the function the model actually sees: its name and schema, that a search reaches the
/// retrieval service with the turn's own scope, and that a failure inside it never reaches the model
/// verbatim.
/// </summary>
public sealed class DocumentToolTests
{
    private readonly IDocumentRetrievalService _retrievalService = Substitute.For<IDocumentRetrievalService>();

    [Fact]
    public void Create_NamesTheToolSoUsageIsNotAttributedToAnMcpServer()
    {
        var function = DocumentTool.Create(Scope(), _retrievalService, NullLogger.Instance);

        // Tool-call usage is credited to an MCP server by longest "{server}_" prefix match, so the first
        // segment of this name must not be a plausible server name.
        Assert.Equal("document_search", function.Name);
        Assert.Equal(DocumentTool.ToolName, function.Name);
        Assert.False(string.IsNullOrWhiteSpace(function.Description));
    }

    [Fact]
    public void Create_DescribesQueryAsRequiredAndDocumentNameAsOptional()
    {
        var function = DocumentTool.Create(Scope(), _retrievalService, NullLogger.Instance);
        var schema = function.JsonSchema;

        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("query", out _));
        Assert.True(properties.TryGetProperty("documentName", out _));

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["query"], required);
    }

    [Fact]
    public void Create_DoesNotExposeTheCancellationTokenToTheModel()
    {
        var function = DocumentTool.Create(Scope(), _retrievalService, NullLogger.Instance);

        var properties = function.JsonSchema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("cancellationToken", out _));
    }

    [Fact]
    public async Task InvokeAsync_PassesTheTurnsOwnScopeRatherThanAnythingFromTheArguments()
    {
        var scope = Scope();
        _retrievalService
            .SearchAsync(Arg.Any<DocumentRetrievalScope>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSearchResult { Query = "refund policy", ResultCount = 0, Results = [] });

        var function = DocumentTool.Create(scope, _retrievalService, NullLogger.Instance);

        await function.InvokeAsync(
            new AIFunctionArguments { ["query"] = "refund policy", ["documentName"] = "handbook.pdf" },
            TestContext.Current.CancellationToken);

        await _retrievalService.Received(1).SearchAsync(
            scope, "refund policy", "handbook.pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_DocumentNameOmitted_SearchesEveryDocument()
    {
        var scope = Scope();
        _retrievalService
            .SearchAsync(Arg.Any<DocumentRetrievalScope>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSearchResult { Query = "refunds", ResultCount = 0, Results = [] });

        var function = DocumentTool.Create(scope, _retrievalService, NullLogger.Instance);

        await function.InvokeAsync(new AIFunctionArguments { ["query"] = "refunds" }, TestContext.Current.CancellationToken);

        await _retrievalService.Received(1).SearchAsync(scope, "refunds", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_Matches_SerializesCitationsAndTextForTheModel()
    {
        _retrievalService
            .SearchAsync(Arg.Any<DocumentRetrievalScope>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSearchResult
            {
                Query = "refunds",
                ResultCount = 1,
                Results =
                [
                    new RetrievedPassage
                    {
                        Citation = "handbook.pdf p.12",
                        DocumentName = "handbook.pdf",
                        Page = 12,
                        Score = 0.82,
                        Text = "Refunds are issued within 30 days."
                    }
                ]
            });

        var function = DocumentTool.Create(Scope(), _retrievalService, NullLogger.Instance);

        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["query"] = "refunds" }, TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("handbook.pdf p.12", json, StringComparison.Ordinal);
        Assert.Contains("Refunds are issued within 30 days.", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_RetrievalFails_ReplacesTheMessageWithSomethingSafeToShowTheModel()
    {
        _retrievalService
            .SearchAsync(Arg.Any<DocumentRetrievalScope>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Login failed for user 'sa'. Server=prod-sql-01;Password=hunter2"));

        var function = DocumentTool.Create(Scope(), _retrievalService, NullLogger.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["query"] = "refunds" }, TestContext.Current.CancellationToken).AsTask());

        // Function invocation is configured with detailed errors, so whatever is thrown here is handed
        // to the model as-is. The original message goes to the log and nowhere else.
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-sql-01", exception.Message, StringComparison.Ordinal);
        Assert.Contains("document search", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_TurnCancelled_LetsCancellationStayCancellation()
    {
        _retrievalService
            .SearchAsync(Arg.Any<DocumentRetrievalScope>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var function = DocumentTool.Create(Scope(), _retrievalService, NullLogger.Instance);

        // Turning this into a tool error would have the model try to work around a turn that is already
        // being torn down.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["query"] = "refunds" }, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void Create_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentTool.Create(null!, _retrievalService, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => DocumentTool.Create(Scope(), null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => DocumentTool.Create(Scope(), _retrievalService, null!));
    }

    private static DocumentRetrievalScope Scope() => new(
        Guid.NewGuid(),
        null,
        [new RetrievableDocument(Guid.NewGuid(), DocumentSource.Conversation, "handbook.pdf")]);
}
