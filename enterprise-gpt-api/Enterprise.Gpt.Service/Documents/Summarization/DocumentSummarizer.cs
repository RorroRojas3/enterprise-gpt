using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Conversations.Chat;
using Enterprise.Gpt.Service.Conversations.Tokenization;
using Enterprise.Gpt.Service.Documents.Summarization.Exceptions;
using Enterprise.Gpt.Service.Documents.Summarization.Models;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Prompts.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents.Summarization;

/// <summary>
/// Reduces a document to one summary within the pinned summarizer's own context budget.
/// </summary>
public interface IDocumentSummarizer
{
    /// <summary>
    /// Summarizes a document from the chunk rows ingestion already wrote.
    /// </summary>
    /// <exception cref="ValidationException">The document has no text to summarize, or the run failed.</exception>
    /// <exception cref="SummarizerNotConfiguredException">The summarizer's catalog row is missing or unusable.</exception>
    Task<SummarizationRun> SummarizeDocumentAsync(
        Guid documentId,
        DocumentSources source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reduces several documents' summaries to one overview through the same pipeline.
    /// </summary>
    /// <exception cref="ValidationException">There is nothing to summarize, or the run failed.</exception>
    /// <exception cref="SummarizerNotConfiguredException">The summarizer's catalog row is missing or unusable.</exception>
    Task<SummarizationRun> SummarizeDigestAsync(string summaries, CancellationToken cancellationToken);
}

/// <summary>
/// Summarizes in one call when a document fits, and by hierarchical map-reduce with a collapse loop
/// when it does not.
/// </summary>
public sealed partial class DocumentSummarizer(
    ISummarizerModelResolver summarizerModelResolver,
    IChatClientResolver chatClientResolver,
    ITokenEstimatorResolver tokenEstimatorResolver,
    PromptOverheadCalculator promptOverheadCalculator,
    IDocumentTextAssembler assembler,
    IOptions<SummarizationOptions> options,
    ILogger<DocumentSummarizer> logger) : IDocumentSummarizer
{
    private readonly ISummarizerModelResolver _summarizerModelResolver = summarizerModelResolver;
    private readonly IChatClientResolver _chatClientResolver = chatClientResolver;
    private readonly ITokenEstimatorResolver _tokenEstimatorResolver = tokenEstimatorResolver;
    private readonly PromptOverheadCalculator _promptOverheadCalculator = promptOverheadCalculator;
    private readonly IDocumentTextAssembler _assembler = assembler;
    private readonly SummarizationOptions _options = options.Value;
    private readonly ILogger<DocumentSummarizer> _logger = logger;

    /// <inheritdoc />
    public async Task<SummarizationRun> SummarizeDocumentAsync(
        Guid documentId,
        DocumentSources source,
        CancellationToken cancellationToken)
    {
        var text = await _assembler.AssembleAsync(documentId, source, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(
                nameof(documentId),
                "This document has no extracted text to summarize. It may still be processing, or "
                    + "its text extraction may have produced nothing.");
        }

        return await SummarizeCoreAsync(text, SummaryStage.Document, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SummarizationRun> SummarizeDigestAsync(
        string summaries, CancellationToken cancellationToken)
    {
        // Thrown inside the async method so it lands on the task: digests fan out through Task.WhenAll,
        // where a synchronous throw would leave already-started siblings unobserved.
        if (string.IsNullOrWhiteSpace(summaries))
        {
            throw Invalid(nameof(summaries), "There is nothing to summarize.");
        }

        return await SummarizeCoreAsync(summaries, SummaryStage.Digest, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record RunContext(
        SummarizerModel Summarizer,
        ITokenEstimator Estimator,
        IChatClient ChatClient);

    private readonly record struct CallResult(
        string Text,
        long InputTokens,
        long OutputTokens,
        bool Truncated);

    private enum SummaryStage
    {
        Document,
        Digest
    }

    #region Private methods

    /// <remarks>
    /// Every terminal failure leaves as a <see cref="ValidationException"/>, or users would see a bare 500.
    /// The original is logged, not shown, since provider messages can carry deployment names or request ids.
    /// </remarks>
    private async Task<SummarizationRun> SummarizeCoreAsync(
        string text,
        SummaryStage stage,
        CancellationToken cancellationToken)
    {
        // Outside the try so the failure log still has it.
        var deployment = "unresolved";

        try
        {
            return await RunAsync(text, stage, name => deployment = name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (ValidationException or SummarizerNotConfiguredException)
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Only the caller's own cancellation passes through; any other is a provider failure, and
            // escaping would report it as though the user had abandoned the request.
            LogSummarizationRunFailed(_logger, ex, deployment);

            throw Invalid("summary", "This document could not be summarized. Please try again.");
        }
    }

    private async Task<SummarizationRun> RunAsync(
        string text,
        SummaryStage stage,
        Action<string> reportDeployment,
        CancellationToken cancellationToken)
    {
        var summarizer = await _summarizerModelResolver.ResolveAsync(cancellationToken)
            .ConfigureAwait(false);

        reportDeployment(summarizer.DeploymentName);

        var estimator = _tokenEstimatorResolver.Resolve(summarizer.ProviderId);
        var chatClient = _chatClientResolver.Resolve(summarizer.ProviderId);

        var context = new RunContext(summarizer, estimator, chatClient);
        var documentTokens = CountTokens(context, text);

        // Sized against this stage's own framing, since the question is whether the text fits that prompt.
        var singleCallPrompt = stage is SummaryStage.Digest
            ? SummarizationPrompts.BuildDigest(text)
            : SummarizationPrompts.BuildSinglePass(text);

        var singleCallBudget = ComputeBudget(context, singleCallPrompt.Instructions);

        if (documentTokens <= singleCallBudget.PerCallTokens)
        {
            var single = await CallAsync(context, singleCallPrompt, cancellationToken)
                .ConfigureAwait(false);

            return BuildRun(context, single.Text, [single], mapUnitCount: 0, collapsePasses: 0);
        }

        // An overflowing digest reuses the document map/reduce framing; the path is too rare for its own.
        return await MapReduceAsync(context, text, documentTokens, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SummarizationRun> MapReduceAsync(
        RunContext context,
        string text,
        long documentTokens,
        CancellationToken cancellationToken)
    {
        var mapBudget = ComputeBudget(context, SummarizationPrompts.BuildMap(1, 1, "x").Instructions);
        var units = MapUnitSplitter.Split(
            text, mapBudget.MapUnitTokens, context.Estimator, context.Summarizer.DeploymentName,
            cancellationToken);

        if (units.Count == 0)
        {
            throw Invalid("summary", "This document has no text to summarize.");
        }

        // Checked before dispatch, so an oversized document costs nothing rather than a partial run.
        if (units.Count > _options.MaxMapUnits)
        {
            throw Invalid(
                "summary",
                $"This document is too large to summarize: it would need {units.Count} passes and "
                    + $"the limit is {_options.MaxMapUnits}.");
        }

        LogSummarizingOversizedDocument(_logger, documentTokens, units.Count, mapBudget.MapUnitTokens);

        var (mapped, mapCalls) = await RunMapPhaseAsync(context, units, cancellationToken)
            .ConfigureAwait(false);

        var (partials, collapsePasses, collapseCalls) = await RunCollapseLoopAsync(
            context, mapped, cancellationToken).ConfigureAwait(false);

        List<CallResult> calls = [.. mapCalls, .. collapseCalls];

        var reduce = await CallAsync(
            context, SummarizationPrompts.BuildReduce(partials), cancellationToken)
            .ConfigureAwait(false);

        calls.Add(reduce);

        return BuildRun(context, reduce.Text, calls, units.Count, collapsePasses);
    }

    /// <remarks>
    /// A failing unit must not cancel siblings already in flight, but queued units are skipped once any unit
    /// fails, since the run is lost anyway; hence the flag is checked after the gate, not before.
    /// </remarks>
    private async Task<(IReadOnlyList<string> Partials, IReadOnlyList<CallResult> Calls)> RunMapPhaseAsync(
        RunContext context,
        IReadOnlyList<string> units,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        var abandoned = 0;

        var results = await Task.WhenAll(units.Select(async (unit, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (Volatile.Read(ref abandoned) != 0)
                {
                    return default(CallResult?);
                }

                return await CallAsync(
                    context,
                    SummarizationPrompts.BuildMap(index + 1, units.Count, unit),
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref abandoned, 1);
                throw;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        // WhenAll throws if any unit failed, so no result is null here.
        var completed = results.Select(r => r!.Value).ToList();

        return ([.. completed.Select(r => r.Text)], completed);
    }

    /// <remarks>
    /// Without it, a document whose partial summaries overflow the window gets no summary at all.
    /// </remarks>
    private async Task<(IReadOnlyList<string> Partials, int Passes, IReadOnlyList<CallResult> Calls)>
        RunCollapseLoopAsync(
            RunContext context,
            IReadOnlyList<string> mapped,
            CancellationToken cancellationToken)
    {
        var partials = mapped;
        List<CallResult> calls = [];

        // Two prompts, two budgets: the loop exits once partials fit the reduce; passes pack for collapse.
        var reduceBudget = ComputeBudget(
            context, SummarizationPrompts.BuildReduce(["x"]).Instructions);

        var collapseBudget = ComputeBudget(
            context, SummarizationPrompts.BuildCollapse(["x"]).Instructions);

        var passes = 0;

        while (CountTokens(context, string.Join("\n\n", partials)) > reduceBudget.PerCallTokens)
        {
            if (passes >= _options.MaxCollapsePasses)
            {
                throw Invalid(
                    "summary",
                    $"This document could not be reduced to a single summary within "
                        + $"{_options.MaxCollapsePasses} collapse passes. It is too large, or too "
                        + "repetitive, to summarize.");
            }

            passes++;

            // Grouping may leave the count unchanged when one partial fills the budget; the pass still
            // compresses it, and the pass limit above bounds a loop that does not converge.
            var groups = GroupToFit(context, partials, collapseBudget.PerCallTokens);

            LogCollapsePass(_logger, passes, partials.Count, groups.Count);

            List<CallResult> collapsed = [];

            foreach (var group in groups)
            {
                collapsed.Add(await CallAsync(
                    context, SummarizationPrompts.BuildCollapse(group), cancellationToken)
                    .ConfigureAwait(false));
            }

            calls.AddRange(collapsed);
            partials = [.. collapsed.Select(c => c.Text)];
        }

        return (partials, passes, calls);
    }

    private List<List<string>> GroupToFit(RunContext context, IReadOnlyList<string> partials, long budget)
    {
        List<List<string>> groups = [];
        List<string> current = [];
        long currentTokens = 0;

        foreach (var partial in partials)
        {
            var tokens = CountTokens(context, partial);

            if (current.Count > 0 && currentTokens + tokens > budget)
            {
                groups.Add(current);
                current = [];
                currentTokens = 0;
            }

            current.Add(partial);
            currentTokens += tokens;
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    private async Task<CallResult> CallAsync(
        RunContext context,
        SummarizationPrompt prompt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await SendAsync(context, prompt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < _options.MaxCallRetries && IsTransient(ex))
            {
                // Full jitter, so map units that fault together do not retry in lockstep.
                var ceiling = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling.TotalMilliseconds);

                LogSummarizationCallRetrying(_logger, ex, attempt + 1, _options.MaxCallRetries + 1, delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <remarks>
    /// Deliberately narrow: the provider SDK has already retried status and transport failures, honouring
    /// <c>Retry-After</c>, so only what it cannot see is retried here: this call's own deadline and an empty
    /// completion. Anything else cannot succeed, and the map phase multiplies each wasted retry.
    /// </remarks>
    private static bool IsTransient(Exception exception) =>
        exception is TimeoutException or EmptyCompletionException;

    private async Task<CallResult> SendAsync(
        RunContext context,
        SummarizationPrompt prompt,
        CancellationToken cancellationToken)
    {
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callCts.CancelAfter(TimeSpan.FromSeconds(_options.CallTimeoutSeconds));

        ChatOptions chatOptions = new()
        {
            ModelId = context.Summarizer.DeploymentName,
            Instructions = prompt.Instructions
        };

        // Bounds the output of a map phase's many calls; that Azure OpenAI treats it as a hard cap is
        // unverified. Guarded so an unfilled catalog row does not put a cap of zero on the wire.
        if (context.Summarizer.MaxOutputTokens > 0)
        {
            chatOptions.MaxOutputTokens = (int)Math.Clamp(context.Summarizer.MaxOutputTokens, 1, int.MaxValue);
        }

        ChatResponse response;

        try
        {
            response = await context.ChatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt.UserMessage)], chatOptions, callCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the caller's token means cancellation. This call's deadline, or the SDK's network
            // timeout surfacing as TaskCanceledException, is a non-response, not the user giving up.
            throw new TimeoutException(
                $"The summarizer did not respond within {_options.CallTimeoutSeconds} seconds.", ex);
        }

        var text = response.Messages.Count > 0
            ? response.Messages[^1].Text?.Trim() ?? string.Empty
            : string.Empty;

        // Cut-off text is kept, as a retry under the same cap gets the same, and flagged since it is cached.
        var truncated = response.FinishReason == ChatFinishReason.Length;

        if (truncated)
        {
            LogSummarizationCallTruncated(
                _logger, context.Summarizer.DeploymentName, context.Summarizer.MaxOutputTokens);
        }

        if (string.IsNullOrEmpty(text))
        {
            // A reasoning deployment can spend the whole cap before emitting anything visible. A
            // retry might land under it, but not reliably enough to buy on every remaining map unit.
            if (truncated)
            {
                throw Invalid(
                    "summary", "The summarizer spent its entire output budget before writing anything.");
            }

            throw new EmptyCompletionException();
        }

        // The report separates assistant from tool usage; Usage is the fallback for an untracked client.
        var report = response.AdditionalProperties?.TryGetValue(
            ToolTrackingChatClient.UsageReportPropertyName, out ChatUsageReport? usageReport) is true
                ? usageReport
                : null;

        return new CallResult(
            text,
            report?.AssistantUsage.InputTokenCount ?? response.Usage?.InputTokenCount ?? 0,
            report?.AssistantUsage.OutputTokenCount ?? response.Usage?.OutputTokenCount ?? 0,
            truncated);
    }

    private SummarizationBudget ComputeBudget(RunContext context, string instructions) =>
        SummarizationBudgetCalculator.Compute(
            context.Summarizer,
            _promptOverheadCalculator.Estimate(
                messageCount: 1, toolCount: 0, CountTokens(context, instructions)),
            _options.SafetyFraction,
            _options.MapUnitFraction);

    /// <remarks>
    /// A tokenizer fault must not fail a run; counting low sends an oversized document down the single-pass
    /// path, which the provider rejects loudly rather than silently mis-summarizing.
    /// </remarks>
    private long CountTokens(RunContext context, string text)
    {
        try
        {
            return context.Estimator.CountTokens(text, context.Summarizer.DeploymentName);
        }
        catch (Exception ex)
        {
            LogTokenEstimationFailed(_logger, ex);

            return 0;
        }
    }

    private static SummarizationRun BuildRun(
        RunContext context,
        string summary,
        IReadOnlyList<CallResult> calls,
        int mapUnitCount,
        int collapsePasses) =>
        new(summary,
            calls.Sum(c => c.InputTokens),
            calls.Sum(c => c.OutputTokens),
            calls.Count,
            mapUnitCount,
            collapsePasses,
            calls.Any(c => c.Truncated),
            context.Summarizer.ModelId,
            context.Summarizer.DeploymentName,
            SummarizationPrompts.PromptVersion);

    /// <remarks>
    /// The one exception type the background job processor copies verbatim into a job's error message.
    /// </remarks>
    private static ValidationException Invalid(string property, string message) =>
        new([new ValidationFailure(property, message)]);

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "A document summarization run failed against deployment {DeploymentName}")]
    private static partial void LogSummarizationRunFailed(ILogger logger, Exception exception, string deploymentName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summarizing an oversized document of {DocumentTokens} tokens as {UnitCount} map units against a per-unit budget of {UnitBudget} tokens")]
    private static partial void LogSummarizingOversizedDocument(
        ILogger logger, long documentTokens, int unitCount, long unitBudget);

    [LoggerMessage(Level = LogLevel.Information, Message = "Collapse pass {Pass} reducing {PartialCount} partial summaries into {GroupCount}")]
    private static partial void LogCollapsePass(ILogger logger, int pass, int partialCount, int groupCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A summarization call failed on attempt {Attempt} of {MaxAttempts}; retrying in {Delay}")]
    private static partial void LogSummarizationCallRetrying(
        ILogger logger, Exception exception, int attempt, int maxAttempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A summarization call against deployment {DeploymentName} was cut off at its {MaxOutputTokens}-token output cap")]
    private static partial void LogSummarizationCallTruncated(
        ILogger logger, string deploymentName, decimal maxOutputTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token estimation failed while sizing a summarization call")]
    private static partial void LogTokenEstimationFailed(ILogger logger, Exception exception);

    #endregion
}
