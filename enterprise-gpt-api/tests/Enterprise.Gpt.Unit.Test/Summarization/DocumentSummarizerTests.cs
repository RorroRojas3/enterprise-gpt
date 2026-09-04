using System.Globalization;
using System.Text.RegularExpressions;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Service.Tokenization;
using Enterprise.Gpt.Service.Tool;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

public sealed class DocumentSummarizerTests
{
    private static readonly Guid ModelId = Guid.Parse("c36e22ed-262a-47a1-b2ba-06a38355ae0f");
    private const string DeploymentName = "rr-gpt5.6-luna";
    private static readonly Guid DocumentId = Guid.NewGuid();

    // A phrase belonging to one template only, so a test can tell which stage's framing went on the
    // wire without reproducing a whole prompt.
    private const string SinglePassFraming = "# Summarize a document";
    private const string ReduceFraming = "not a further distillation";
    private const string CollapseFraming = "materially shorter";
    private const string DigestFraming = "overview of the set";

    private readonly ISummarizerModelResolver _summarizerModelResolver =
        Substitute.For<ISummarizerModelResolver>();
    private readonly IChatClientResolver _chatClientResolver = Substitute.For<IChatClientResolver>();
    private readonly ITokenEstimatorResolver _tokenEstimatorResolver =
        Substitute.For<ITokenEstimatorResolver>();
    private readonly IDocumentTextAssembler _assembler = Substitute.For<IDocumentTextAssembler>();
    private readonly FakeChatClient _chatClient = new();

    public DocumentSummarizerTests()
    {
        _tokenEstimatorResolver.Resolve(Arg.Any<Guid>()).Returns(new WordTokenEstimator());
        _chatClientResolver.Resolve(Arg.Any<Guid>()).Returns(_chatClient);
        SetSummarizer();
    }

    #region Single pass

    /// <summary>
    /// A document that fits is summarized in exactly one call — a short document is not needlessly
    /// split and recombined.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_DocumentThatFits_MakesExactlyOneCall()
    {
        SetDocumentText(Words(50));

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(1, _chatClient.CallCount);
        Assert.Equal(1, run.ModelCallCount);
        Assert.Equal(0, run.MapUnitCount);
        Assert.Equal(0, run.CollapsePasses);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_SinglePass_SendsTheDeploymentNameAsTheModelId()
    {
        SetDocumentText(Words(50));

        await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentName, _chatClient.CapturedOptions[0].ModelId);
    }

    /// <summary>
    /// The first call site in the codebase to put the option on the wire for any provider. Without
    /// it a map phase's per-call output is unbounded, billed at the summarizer's rate on every one
    /// of potentially hundreds of calls.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_AnyCall_CapsOutputFromTheCatalogRow()
    {
        SetDocumentText(Words(50));

        await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(32_768, _chatClient.CapturedOptions[0].MaxOutputTokens);
    }

    /// <summary>
    /// A summary cut off at the cap is still the best answer the run has, so it is kept rather than
    /// failed — but it is then cached as it stands, which is why the run has to say so.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_CompletionCutOffAtTheOutputCap_KeepsTheTextAndReportsIt()
    {
        SetDocumentText(Words(50));
        _chatClient.FinishReason = ChatFinishReason.Length;

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(run.Truncated);
        Assert.Equal("A summary.", run.Summary);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_CompletionThatEndedOnItsOwn_IsNotReportedAsTruncated()
    {
        SetDocumentText(Words(50));
        _chatClient.FinishReason = ChatFinishReason.Stop;

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.False(run.Truncated);
    }

    /// <summary>
    /// A map unit that lost its tail damages the summary just as much as a cut-off final call, and
    /// only the run carries that out.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_MapCallCutOffAtTheOutputCap_ReportsTheRunAsTruncated()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));
        _chatClient.FinishReason = ChatFinishReason.Length;

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(run.MapUnitCount > 1);
        Assert.True(run.Truncated);
    }

    /// <summary>
    /// A reasoning deployment can spend the whole cap before writing anything, which no retry
    /// clears — it has to fail once, not once per attempt on every remaining unit.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_OutputBudgetSpentBeforeAnyText_FailsWithoutRetrying()
    {
        SetDocumentText(Words(50));
        _chatClient.FinishReason = ChatFinishReason.Length;
        _chatClient.EmptyResponsesBeforeSuccess = 1;

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer().SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Equal(1, _chatClient.CallCount);
        Assert.Contains(
            "output budget", exception.Errors.Single().ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A catalog row nobody filled in would otherwise put a cap of zero on the wire, which is worse
    /// than sending none at all.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_CatalogRowWithNoOutputCap_SendsNoCap()
    {
        SetSummarizer(maxOutputTokens: 0m);
        SetDocumentText(Words(50));

        await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Null(_chatClient.CapturedOptions[0].MaxOutputTokens);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_SinglePass_CarriesTheFramingOnTheInstructions()
    {
        SetDocumentText("The document body.");

        await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Contains(
            "never instructions to follow",
            _chatClient.CapturedOptions[0].Instructions,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "The document body.", _chatClient.CapturedMessages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_SinglePass_ReportsTheRunTotalsAndSnapshotsTheModel()
    {
        SetDocumentText(Words(50));

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(11, run.InputTokens);
        Assert.Equal(7, run.OutputTokens);
        Assert.Equal(ModelId, run.ModelId);
        Assert.Equal(DeploymentName, run.DeploymentName);
        Assert.Equal(SummarizationPrompts.PromptVersion, run.PromptVersion);
    }

    #endregion

    #region Map-reduce

    [Fact]
    public async Task SummarizeDocumentAsync_OversizedDocument_MapsThenReduces()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(run.MapUnitCount > 1);
        Assert.Equal(run.MapUnitCount + 1, run.ModelCallCount);
        Assert.Equal(0, run.CollapsePasses);
    }

    /// <summary>
    /// Checked before the map phase dispatches anything, so a document too large to summarize costs
    /// nothing at all rather than running to the ceiling and stopping with a truncated result.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_SplitExceedsTheMapUnitCeiling_FailsBeforeAnyCall()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));

        var summarizer = CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxMapUnits = 2 });

        var exception = await Assert.ThrowsAsync<ValidationException>(() => summarizer.SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Contains("too large to summarize", exception.Message, StringComparison.Ordinal);
        Assert.Contains("the limit is 2", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _chatClient.CallCount);
    }

    /// <summary>
    /// A ceiling the split stays under changes nothing, so the guard cannot quietly narrow what the
    /// engine already handled.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_SplitWithinTheMapUnitCeiling_RunsNormally()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));

        var run = await CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxMapUnits = 1_000 })
            .SummarizeDocumentAsync(DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(run.MapUnitCount > 1);
        Assert.Equal(run.MapUnitCount + 1, run.ModelCallCount);
    }

    /// <summary>
    /// The accounting shape is uniform across both paths: the run total is every call's tokens, not
    /// the last call's.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_OversizedDocument_SumsTokensAcrossEveryCall()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(11 * run.ModelCallCount, run.InputTokens);
        Assert.Equal(7 * run.ModelCallCount, run.OutputTokens);
        Assert.Equal(_chatClient.CallCount, run.ModelCallCount);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_MapPhase_NumbersEachUnitInItsFrame()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Contains(
            _chatClient.CapturedOptions,
            options => options.Instructions!.Contains(
                $"part 1 of {run.MapUnitCount}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The one thing concurrency could silently break. Map units complete out of order by design,
    /// so the reduce prompt has to carry them in document order regardless of who finished first.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_MapPhase_ReducesPartialsInDocumentOrder()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));
        _chatClient.EchoUnitNumber = true;

        // Later units answer fastest, so completion order is the reverse of dispatch order.
        _chatClient.DelayByUnitNumber = true;

        var run = await CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxConcurrency = 8 })
            .SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        var reducePrompt = _chatClient.CapturedMessages[^1];
        var positions = Enumerable.Range(1, run.MapUnitCount)
            .Select(n => reducePrompt.IndexOf($"unit {n} summary", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    /// <summary>
    /// A unit already in flight is left to finish, but the run is doomed once one unit fails, so
    /// units still queued behind the gate must not be dispatched — on a large document that is
    /// hundreds of calls bought for a result nobody reads.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_UnitFailure_StopsDispatchingQueuedUnits()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 60, wordsEach: 100));
        _chatClient.FailPermanentlyFromCall = 2;

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer(new SummarizationOptions
            {
                ModelId = ModelId,
                MaxConcurrency = 2,
                MaxCallRetries = 0
            }).SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.True(
            _chatClient.CallCount < 10,
            $"{_chatClient.CallCount} calls were dispatched after a unit had already failed.");
    }

    /// <summary>
    /// Bounded so one document's map phase cannot monopolise the background job processor's own
    /// gate, which summarization shares with document ingestion.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_MapPhase_NeverExceedsTheConfiguredConcurrency()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 40, wordsEach: 100));
        _chatClient.CallDelay = TimeSpan.FromMilliseconds(20);

        var run = await CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxConcurrency = 3 })
            .SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(run.MapUnitCount > 3, "The document did not produce enough units to contend.");
        Assert.True(
            _chatClient.PeakConcurrency <= 3,
            $"Peak concurrency reached {_chatClient.PeakConcurrency} against a limit of 3.");
    }

    #endregion

    #region Collapse loop

    /// <summary>
    /// What plain map-reduce lacks. Without the loop a document whose own partial summaries overflow
    /// the window produces no summary at all.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_PartialSummariesThatOverflow_CollapseBeforeReducing()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 60, wordsEach: 100));
        _chatClient.ResponseShrinkRatio = 0.4;

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(run.CollapsePasses > 0);
        Assert.True(run.ModelCallCount > run.MapUnitCount + 1);
    }

    /// <summary>
    /// The loop is conditional, not a mandatory extra call: map output that already fits goes
    /// straight to the final reduce.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_PartialSummariesThatFit_PerformNoCollapsePasses()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 12, wordsEach: 100));

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(0, run.CollapsePasses);
    }

    /// <summary>
    /// A collapse pass has to come back shorter or the loop never converges, while the reduce it
    /// feeds is the summary that gets cached and is told to keep what it was given. Pointing both
    /// stages at one template is the regression this catches.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_CollapsePass_CompressesBeforeTheFinalReduce()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 60, wordsEach: 100));
        _chatClient.ResponseShrinkRatio = 0.4;

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(run.CollapsePasses > 0);

        var framings = _chatClient.CapturedOptions
            .Select(o => o.Instructions ?? string.Empty)
            .ToList();

        Assert.Contains(ReduceFraming, framings[^1], StringComparison.Ordinal);
        Assert.Contains(framings[..^1], f => f.Contains(CollapseFraming, StringComparison.Ordinal));
        Assert.DoesNotContain(framings[..^1], f => f.Contains(ReduceFraming, StringComparison.Ordinal));
    }

    /// <summary>
    /// A pathological document must fail naming the limit rather than loop until an unrelated
    /// timeout kills it silently.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_PartialSummariesThatNeverShrink_FailsNamingTheLimit()
    {
        SetSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 0m);
        SetDocumentText(Paragraphs(count: 60, wordsEach: 100));
        _chatClient.ResponseText = Words(800);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer().SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Contains("summarize", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Failures

    [Fact]
    public async Task SummarizeDocumentAsync_DocumentWithNoChunks_FailsWithoutCallingTheModel()
    {
        SetDocumentText(string.Empty);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer().SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Equal(0, _chatClient.CallCount);
        Assert.Contains("no extracted text", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The per-request half of the invariant: a startup check cannot see an administrator editing
    /// the window back to zero while the application is already running.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_UnconfiguredContextWindow_RefusesWithoutCallingTheModel()
    {
        SetSummarizer(contextWindowSize: 0m);
        SetDocumentText(Words(50));

        var exception = await Assert.ThrowsAsync<SummarizerNotConfiguredException>(
            () => CreateSummarizer().SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Equal(0, _chatClient.CallCount);
        Assert.Equal(ModelId, exception.ModelId);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_TransientFailure_RetriesUpToTheConfiguredCount()
    {
        SetDocumentText(Words(50));
        _chatClient.FailuresBeforeSuccess = 2;

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(3, _chatClient.CallCount);
        Assert.Equal(1, run.ModelCallCount);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_FailureBeyondTheRetryCount_FailsTheRun()
    {
        SetDocumentText(Words(50));
        _chatClient.FailuresBeforeSuccess = 99;

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxCallRetries = 1 })
                .SummarizeDocumentAsync(
                    DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Equal(2, _chatClient.CallCount);
    }

    /// <summary>
    /// An empty completion is a plausible blip rather than a permanent fault, and without a retry one
    /// blank response out of a several-hundred-call map phase would fail the whole document.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_EmptyCompletion_IsRetried()
    {
        SetDocumentText(Words(50));
        _chatClient.EmptyResponsesBeforeSuccess = 1;

        var run = await CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(2, _chatClient.CallCount);
        Assert.Equal(1, run.ModelCallCount);
    }

    /// <summary>
    /// A bad credential, a repointed deployment name or a content-filter rejection cannot succeed on
    /// a second attempt, and a map phase would multiply the wasted calls by the unit count.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_NonTransientFailure_IsNotRetried()
    {
        SetDocumentText(Words(50));
        _chatClient.FailPermanentlyFromCall = 1;

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxCallRetries = 3 })
                .SummarizeDocumentAsync(
                    DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Equal(1, _chatClient.CallCount);
    }

    /// <summary>
    /// A provider message can carry a deployment name or a request id, so the reason a user reads is
    /// the platform's own, with the original left to the log.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_ProviderFailure_DoesNotLeakTheProviderMessage()
    {
        SetDocumentText(Words(50));
        _chatClient.FailPermanentlyFromCall = 1;

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxCallRetries = 0 })
                .SummarizeDocumentAsync(
                    DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("provider fault", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be summarized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stalled call must not hold a processing slot indefinitely, and the timeout must be
    /// reported as a timeout rather than as the caller's own cancellation.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_CallExceedingTheTimeout_FailsWithoutHanging()
    {
        SetDocumentText(Words(50));
        _chatClient.CallDelay = TimeSpan.FromSeconds(30);

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer(new SummarizationOptions
            {
                ModelId = ModelId,
                CallTimeoutSeconds = 1,
                MaxCallRetries = 0
            }).SummarizeDocumentAsync(
                DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));

        Assert.Equal(1, _chatClient.CallCount);
    }

    /// <summary>
    /// A job nobody is waiting on must stop rather than keep billing, and the caller's own
    /// cancellation is never retried.
    /// </summary>
    [Fact]
    public async Task SummarizeDocumentAsync_CallerCancellation_StopsWithoutRetrying()
    {
        SetDocumentText(Words(50));
        _chatClient.CallDelay = TimeSpan.FromSeconds(30);

        using var cts = new CancellationTokenSource();
        var task = CreateSummarizer().SummarizeDocumentAsync(
            DocumentId, DocumentSource.Conversation, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, _chatClient.CallCount);
    }

    [Fact]
    public async Task SummarizeDocumentAsync_ModelReturningNoText_FailsTheRun()
    {
        SetDocumentText(Words(50));
        _chatClient.ResponseText = string.Empty;

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer(new SummarizationOptions { ModelId = ModelId, MaxCallRetries = 0 })
                .SummarizeDocumentAsync(
                    DocumentId, DocumentSource.Conversation, TestContext.Current.CancellationToken));
    }

    #endregion

    #region SummarizeDigestAsync

    /// <summary>
    /// The seam a conversation digest reduces through — the same pipeline, run over per-document
    /// summaries instead of a document's own text.
    /// </summary>
    [Fact]
    public async Task SummarizeDigestAsync_Summaries_DigestsWithoutReadingAnyDocument()
    {
        var run = await CreateSummarizer().SummarizeDigestAsync(
            Words(50), TestContext.Current.CancellationToken);

        Assert.Equal(1, run.ModelCallCount);
        await _assembler.DidNotReceiveWithAnyArgs()
            .AssembleAsync(default, default, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A digest is an overview of a scope, not a record of everything in it — the one framing that
    /// asks for less rather than more.
    /// </summary>
    [Fact]
    public async Task SummarizeDigestAsync_Summaries_CarryTheDigestFramingNotTheDocumentOne()
    {
        await CreateSummarizer().SummarizeDigestAsync(
            "Document 1 of 1: handbook.pdf\nRefunds within 30 days.",
            TestContext.Current.CancellationToken);

        var framing = _chatClient.CapturedOptions[0].Instructions ?? string.Empty;

        Assert.Contains(DigestFraming, framing, StringComparison.Ordinal);
        Assert.DoesNotContain(SinglePassFraming, framing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SummarizeDigestAsync_BlankSummaries_Fails(string text)
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => CreateSummarizer().SummarizeDigestAsync(text, TestContext.Current.CancellationToken));
    }

    #endregion

    #region Helpers

    private DocumentSummarizer CreateSummarizer(SummarizationOptions? options = null)
    {
        return new DocumentSummarizer(
            _summarizerModelResolver,
            _chatClientResolver,
            _tokenEstimatorResolver,
            new PromptOverheadCalculator(Options.Create(new TokenEstimationOptions())),
            _assembler,
            Options.Create(options ?? new SummarizationOptions { ModelId = ModelId }),
            NullLogger<DocumentSummarizer>.Instance);
    }

    private void SetSummarizer(decimal contextWindowSize = 1_000_000m, decimal maxOutputTokens = 32_768m)
    {
        _summarizerModelResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(
            new SummarizerModel(
                ModelId, Providers.AzureOpenAI, DeploymentName, contextWindowSize, maxOutputTokens, null, null));
    }

    private void SetDocumentText(string text)
    {
        _assembler.AssembleAsync(DocumentId, Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(text);
    }

    private static string Words(int count) =>
        string.Join(" ", Enumerable.Range(0, count).Select(i => $"w{i}"));

    private static string Paragraphs(int count, int wordsEach) =>
        string.Join(
            "\n\n",
            Enumerable.Range(0, count).Select(
                p => string.Join(" ", Enumerable.Range(0, wordsEach).Select(w => $"p{p}w{w}"))));

    private sealed class WordTokenEstimator : ITokenEstimator
    {
        public long CountTokens(string? text, string? deploymentName) =>
            string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Records what each call was given and how many ran at once. A substituted
    /// <see cref="IChatClient"/> cannot script a response, so the suite hand-rolls its fakes.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly Lock _sync = new();
        private int _inFlight;
        private int _failuresRemaining = -1;

        public List<ChatOptions> CapturedOptions { get; } = [];

        public List<string> CapturedMessages { get; } = [];

        public int CallCount { get; private set; }

        public int PeakConcurrency { get; private set; }

        public string ResponseText { get; set; } = "A summary.";

        /// <summary>
        /// Gets or sets the share of its input a response carries, modelling a summarizer that
        /// actually compresses. Left at zero, every response is <see cref="ResponseText"/>
        /// regardless of input — a summarizer that never shrinks.
        /// </summary>
        public double ResponseShrinkRatio { get; set; }

        public TimeSpan CallDelay { get; set; }

        public int FailuresBeforeSuccess { get; set; }

        /// <summary>
        /// Gets or sets the call ordinal from which every call fails permanently, standing in for a
        /// fault no retry can clear. Zero leaves every call succeeding.
        /// </summary>
        public int FailPermanentlyFromCall { get; set; }

        /// <summary>
        /// Gets or sets how many calls answer with no text before one answers normally.
        /// </summary>
        public int EmptyResponsesBeforeSuccess { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a response echoes the map unit's position, so a
        /// test can assert what order the partials reach the reduce prompt in.
        /// </summary>
        public bool EchoUnitNumber { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether later units answer faster, making completion
        /// order the reverse of dispatch order.
        /// </summary>
        public bool DelayByUnitNumber { get; set; }

        /// <summary>
        /// Gets or sets the finish reason every answered call carries. Null stands in for a
        /// provider that reports none.
        /// </summary>
        public ChatFinishReason? FinishReason { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            bool shouldFail;

            int ordinal;

            lock (_sync)
            {
                ordinal = ++CallCount;
                CapturedOptions.Add(options!);
                CapturedMessages.Add(string.Join("\n", messages.Select(m => m.Text)));

                _inFlight++;
                PeakConcurrency = Math.Max(PeakConcurrency, _inFlight);

                if (_failuresRemaining < 0)
                {
                    _failuresRemaining = FailuresBeforeSuccess;
                }

                shouldFail = _failuresRemaining > 0;

                if (shouldFail)
                {
                    _failuresRemaining--;
                }
            }

            var unitNumber = UnitNumberOf(options?.Instructions);

            try
            {
                if (DelayByUnitNumber && unitNumber > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 120 - unitNumber * 8)), cancellationToken);
                }
                else if (CallDelay > TimeSpan.Zero)
                {
                    await Task.Delay(CallDelay, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }

                if (FailPermanentlyFromCall > 0 && ordinal >= FailPermanentlyFromCall)
                {
                    throw new InvalidOperationException("A permanent provider fault.");
                }

                if (shouldFail)
                {
                    // A timeout, because the engine deliberately retries only what the provider
                    // SDK's own policy cannot already see.
                    throw new TimeoutException("A transient provider fault.");
                }

                if (ordinal <= EmptyResponsesBeforeSuccess)
                {
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty))
                    {
                        Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 0 },
                        FinishReason = FinishReason
                    };
                }

                if (EchoUnitNumber && unitNumber > 0)
                {
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"unit {unitNumber} summary"))
                    {
                        Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 },
                        FinishReason = FinishReason
                    };
                }

                return new ChatResponse(new ChatMessage(ChatRole.Assistant, Respond(messages)))
                {
                    Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 },
                    FinishReason = FinishReason
                };
            }
            finally
            {
                lock (_sync)
                {
                    _inFlight--;
                }
            }
        }

        /// <remarks>
        /// The map frame is the only one that names a position, so this is also how a map call is
        /// told apart from a reduce call.
        /// </remarks>
        private static int UnitNumberOf(string? instructions)
        {
            var match = instructions is null
                ? Match.Empty
                : Regex.Match(instructions, @"part (\d+) of \d+", RegexOptions.IgnoreCase);

            return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private string Respond(IEnumerable<ChatMessage> messages)
        {
            if (ResponseShrinkRatio <= 0)
            {
                return ResponseText;
            }

            var inputWords = string.Join(" ", messages.Select(m => m.Text))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            return Words(Math.Max(10, (int)(inputWords * ResponseShrinkRatio)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Summarization never streams.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    #endregion
}
