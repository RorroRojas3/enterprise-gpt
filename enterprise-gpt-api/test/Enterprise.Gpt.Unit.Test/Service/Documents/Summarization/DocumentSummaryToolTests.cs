using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Service.Tool;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers the function the model actually sees: its name and schema, how it resolves a document
/// name, and that neither a provider's message nor a stalled run reaches the model as anything but
/// a sentence it can act on.
/// </summary>
public sealed class DocumentSummaryToolTests
{
    private const int TimeoutSeconds = 300;

    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid HandbookId = Guid.NewGuid();
    private static readonly Guid PolicyId = Guid.NewGuid();

    private readonly IDocumentSummaryService _summaryService = Substitute.For<IDocumentSummaryService>();

    [Fact]
    public void Create_NamesTheToolSoUsageIsNotAttributedToAnMcpServer()
    {
        var function = CreateFunction();

        // Tool-call usage is credited to an MCP server by longest "{server}_" prefix match, so the
        // first segment of this name must not be a plausible server name.
        Assert.Equal("document_summarize", function.Name);
        Assert.Equal(DocumentSummaryTool.ToolName, function.Name);
        Assert.False(string.IsNullOrWhiteSpace(function.Description));
    }

    [Fact]
    public void Create_DescribesDocumentNameAsOptional()
    {
        var schema = CreateFunction().JsonSchema;

        Assert.True(schema.GetProperty("properties").TryGetProperty("documentName", out _));

        // Omitting the name is how the model asks for a digest, so it must not be required.
        Assert.False(schema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0);
    }

    [Fact]
    public void Create_DoesNotExposeTheCancellationTokenToTheModel()
    {
        var properties = CreateFunction().JsonSchema.GetProperty("properties");

        Assert.False(properties.TryGetProperty("cancellationToken", out _));
    }

    [Fact]
    public async Task InvokeAsync_NameMatchesOneDocument_SummarizesThatDocumentOnly()
    {
        _summaryService
            .GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummaryResult("The handbook covers refunds.", FromCache: false, ModelCallCount: 1));

        var function = CreateFunction();

        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = "handbook.pdf" }, TestContext.Current.CancellationToken);

        await _summaryService.Received(1).GetOrCreateAsync(
            ConversationId, HandbookId, DocumentSource.Conversation, Arg.Any<CancellationToken>());

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("The handbook covers refunds.", json, StringComparison.Ordinal);
        Assert.Contains("handbook.pdf", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scope is the turn's own, never anything the model supplied, so a hallucinated argument
    /// cannot widen what a summary can reach.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ProjectDocument_PassesItsOwnSourceThrough()
    {
        var projectDocumentId = Guid.NewGuid();

        _summaryService
            .GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummaryResult("A project document.", FromCache: true, ModelCallCount: 0));

        var scope = new DocumentRetrievalScope(
            ConversationId,
            Guid.NewGuid(),
            [new RetrievableDocument(projectDocumentId, DocumentSource.Project, "shared.docx")]);

        var function = DocumentSummaryTool.Create(
            scope, ConversationId, _summaryService, TimeoutSeconds, NullLogger.Instance);

        await function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = "shared.docx" }, TestContext.Current.CancellationToken);

        await _summaryService.Received(1).GetOrCreateAsync(
            ConversationId, projectDocumentId, DocumentSource.Project, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_NameMatchesNothing_RefusesWithoutSpendingAnything()
    {
        var function = CreateFunction();

        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = "invoice.xlsx" }, TestContext.Current.CancellationToken);

        await _summaryService.DidNotReceiveWithAnyArgs()
            .GetOrCreateAsync(default, default, default, Arg.Any<CancellationToken>());
        await _summaryService.DidNotReceiveWithAnyArgs().CreateDigestAsync(default, default!, Arg.Any<CancellationToken>());

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("handbook.pdf", json, StringComparison.Ordinal);
        Assert.Contains("policy.pdf", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"summary\"", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deliberately unlike <c>document_search</c>, which widens to every document on an ambiguous
    /// name. A wider search is cheap; summarizing the wrong document is not.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NameMatchesSeveralDocuments_RefusesRatherThanWidening()
    {
        var function = CreateFunction();

        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = ".pdf" }, TestContext.Current.CancellationToken);

        await _summaryService.DidNotReceiveWithAnyArgs()
            .GetOrCreateAsync(default, default, default, Arg.Any<CancellationToken>());
        await _summaryService.DidNotReceiveWithAnyArgs().CreateDigestAsync(default, default!, Arg.Any<CancellationToken>());

        Assert.Contains("more than one", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_NameOmitted_DigestsEveryDocumentInScope()
    {
        _summaryService
            .CreateDigestAsync(Arg.Any<Guid>(), Arg.Any<DocumentRetrievalScope>(), Arg.Any<CancellationToken>())
            .Returns("Both documents concern refunds.");

        var function = CreateFunction();

        var result = await function.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        await _summaryService.Received(1).CreateDigestAsync(
            ConversationId, Arg.Any<DocumentRetrievalScope>(), Arg.Any<CancellationToken>());

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("Both documents concern refunds.", json, StringComparison.Ordinal);
        Assert.Contains("digest", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A digest over one document would buy a second model call to reduce one summary to itself.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NameOmittedAndOneDocumentInScope_SummarizesItDirectly()
    {
        _summaryService
            .GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummaryResult("Only one document here.", FromCache: false, ModelCallCount: 1));

        var scope = new DocumentRetrievalScope(
            ConversationId, null, [new RetrievableDocument(HandbookId, DocumentSource.Conversation, "handbook.pdf")]);

        var function = DocumentSummaryTool.Create(
            scope, ConversationId, _summaryService, TimeoutSeconds, NullLogger.Instance);

        var result = await function.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        await _summaryService.Received(1).GetOrCreateAsync(
            ConversationId, HandbookId, DocumentSource.Conversation, Arg.Any<CancellationToken>());
        await _summaryService.DidNotReceiveWithAnyArgs().CreateDigestAsync(default, default!, Arg.Any<CancellationToken>());

        Assert.Contains("\"document\"", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The engine and the summary service both funnel terminal failures into a
    /// <see cref="ValidationException"/> carrying a sentence written for a person, which is why it
    /// is relayed rather than replaced.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_RunRefused_RelaysTheSanitizedReasonToTheModel()
    {
        _summaryService
            .GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Throws(new ValidationException([new ValidationFailure("summary", "This document is too large to summarize.")]));

        var function = CreateFunction();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = "handbook.pdf" },
            TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("too large to summarize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_RunFails_ReplacesTheMessageWithSomethingSafeToShowTheModel()
    {
        _summaryService
            .GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Login failed for user 'sa'. Server=prod-sql-01;Password=hunter2"));

        var function = CreateFunction();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = "handbook.pdf" },
            TestContext.Current.CancellationToken).AsTask());

        // Function invocation is configured with detailed errors, so whatever is thrown here is
        // handed to the model as-is. The original message goes to the log and nowhere else.
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-sql-01", exception.Message, StringComparison.Ordinal);
        Assert.Contains("summarized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tool's own deadline, not the caller's. Reported to the model rather than thrown at the
    /// turn, because a summary that took too long is something the user can be told about.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ExceedsItsOwnDeadline_TellsTheModelRatherThanFailingTheTurn()
    {
        _summaryService
            .GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await Task.Delay(Timeout.Infinite, callInfo.ArgAt<CancellationToken>(3));
                return new DocumentSummaryResult(string.Empty, false, 0);
            });

        var function = DocumentSummaryTool.Create(
            Scope(), ConversationId, _summaryService, toolTimeoutSeconds: 1, NullLogger.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = "handbook.pdf" },
            TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("too long", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_TurnCancelled_LetsCancellationStayCancellation()
    {
        using var cts = new CancellationTokenSource();

        _summaryService
            .GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(3).ThrowIfCancellationRequested();
                return new DocumentSummaryResult(string.Empty, false, 0);
            });

        var function = CreateFunction();

        // Turning this into a tool error would have the model work around a turn already being torn
        // down.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => function.InvokeAsync(
            new AIFunctionArguments { ["documentName"] = "handbook.pdf" }, cts.Token).AsTask());
    }

    [Fact]
    public void Create_InvalidArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentSummaryTool.Create(
            null!, ConversationId, _summaryService, TimeoutSeconds, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => DocumentSummaryTool.Create(
            Scope(), ConversationId, null!, TimeoutSeconds, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => DocumentSummaryTool.Create(
            Scope(), ConversationId, _summaryService, TimeoutSeconds, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentSummaryTool.Create(
            Scope(), ConversationId, _summaryService, 0, NullLogger.Instance));
    }

    private AIFunction CreateFunction() => DocumentSummaryTool.Create(
        Scope(), ConversationId, _summaryService, TimeoutSeconds, NullLogger.Instance);

    private static DocumentRetrievalScope Scope() => new(
        ConversationId,
        null,
        [
            new RetrievableDocument(HandbookId, DocumentSource.Conversation, "handbook.pdf"),
            new RetrievableDocument(PolicyId, DocumentSource.Conversation, "policy.pdf")
        ]);
}
