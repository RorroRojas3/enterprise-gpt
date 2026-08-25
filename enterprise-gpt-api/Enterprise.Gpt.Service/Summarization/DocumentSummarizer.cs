using Andes.Extensions.AI;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Enterprise.Gpt.Service.Tool;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Summarization;

/// <summary>
/// What one summarization run produced, and what it cost.
/// </summary>
/// <param name="Summary">The canonical summary text.</param>
/// <param name="InputTokens">Input tokens across every call the run made.</param>
/// <param name="OutputTokens">Output tokens across every call the run made.</param>
/// <param name="ModelCallCount">
/// Every map call, plus every collapse-pass call, plus the final reduce. Exactly one for a document
/// that fitted a single pass.
/// </param>
/// <param name="MapUnitCount">How many units the document was split into. Zero on the single-pass path.</param>
/// <param name="CollapsePasses">How many times the partial summaries had to be re-summarized. Usually zero.</param>
/// <param name="ModelId">The summarizer's catalog row id, as it stood when the run started.</param>
/// <param name="DeploymentName">The deployment that actually served the run.</param>
/// <param name="PromptVersion">The prompt generation the summary was produced under.</param>
/// <remarks>
/// <paramref name="ModelId"/> and <paramref name="DeploymentName"/> are snapshotted rather than
/// looked up later, for the reason <c>ConversationUsage.DeploymentName</c> is: repointing the
/// catalog row afterwards must not rewrite what an already-generated summary says produced it.
/// </remarks>
public sealed record SummarizationRun(
    string Summary,
    long InputTokens,
    long OutputTokens,
    int ModelCallCount,
    int MapUnitCount,
    int CollapsePasses,
    Guid ModelId,
    string DeploymentName,
    string PromptVersion);

/// <summary>
/// Reduces a document to one summary within the pinned summarizer's own context budget.
/// </summary>
public interface IDocumentSummarizer
{
    /// <summary>
    /// Summarizes a document from the chunk rows ingestion already wrote.
    /// </summary>
    /// <param name="documentId">The document's identifier.</param>
    /// <param name="source">Which document family the id belongs to.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The summary and what producing it cost.</returns>
    /// <exception cref="ValidationException">The document has no text to summarize, or the run failed.</exception>
    /// <exception cref="SummarizerNotConfiguredException">The summarizer's catalog row is missing or unusable.</exception>
    Task<SummarizationRun> SummarizeDocumentAsync(
        Guid documentId,
        DocumentSource source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Summarizes arbitrary text through the same pipeline.
    /// </summary>
    /// <param name="text">The text to summarize.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The summary and what producing it cost.</returns>
    /// <exception cref="ValidationException">There is no text to summarize, or the run failed.</exception>
    /// <exception cref="SummarizerNotConfiguredException">The summarizer's catalog row is missing or unusable.</exception>
    /// <remarks>
    /// The seam a conversation-level digest reduces through: a digest is not a fourth algorithm, it
    /// is this same pipeline run over per-document summaries instead of a document's own text.
    /// </remarks>
    Task<SummarizationRun> SummarizeTextAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// Summarizes in one call when a document fits, and by hierarchical map-reduce with a collapse loop
/// when it does not.
/// </summary>
/// <param name="summarizerModelResolver">Reads the pinned summarizer's catalog row.</param>
/// <param name="chatClientResolver">Resolves the summarizer's provider-specific chat client.</param>
/// <param name="tokenEstimatorResolver">Resolves the token estimator for that provider.</param>
/// <param name="promptOverheadCalculator">Accounts for the prompt tokens belonging to no message.</param>
/// <param name="assembler">Reassembles a document's text from its chunk rows.</param>
/// <param name="options">The bound summarization options.</param>
/// <param name="logger">The logger.</param>
public sealed class DocumentSummarizer(
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

    #region Public methods

    /// <inheritdoc />
    public async Task<SummarizationRun> SummarizeDocumentAsync(
        Guid documentId,
        DocumentSource source,
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

        return await SummarizeCoreAsync(text, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SummarizationRun> SummarizeTextAsync(string text, CancellationToken cancellationToken)
    {
        // async rather than a bare Task-returning guard, so the failure lands on the returned task.
        // A digest fans several of these out through Task.WhenAll, where a synchronous throw would
        // abandon the sibling tasks it had already started, unobserved.
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(nameof(text), "There is nothing to summarize.");
        }

        return await SummarizeCoreAsync(text, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Private methods

    /// <remarks>
    /// Every terminal failure leaves as a <see cref="ValidationException"/>, because that is the one
    /// type the background job processor copies verbatim into a job's error message. A provider
    /// exception allowed to escape as itself reaches the user as a generic 500 with the message
    /// suppressed — so the failure most likely to happen in production would be the one whose reason
    /// nobody ever reads. The original is logged rather than shown, since a provider message can
    /// carry a deployment name or a request id.
    /// </remarks>
    private async Task<SummarizationRun> SummarizeCoreAsync(string text, CancellationToken cancellationToken)
    {
        // Named outside the try so the log line still has it when the failure happens deep in a run.
        var deployment = "unresolved";

        try
        {
            return await RunAsync(text, name => deployment = name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (ValidationException or SummarizerNotConfiguredException)
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Only a cancellation the caller actually asked for passes through. One that nobody's
            // token explains is a provider failure wearing a cancellation's clothes, and letting it
            // escape would report it to the user as though they had abandoned the request.
            _logger.LogError(
                ex,
                "A document summarization run failed against deployment {DeploymentName}",
                deployment);

            throw Invalid("summary", "This document could not be summarized. Please try again.");
        }
    }

    private async Task<SummarizationRun> RunAsync(
        string text,
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

        // Sized against the single-pass frame specifically: each stage's framing differs, and the
        // decision this budget serves is whether the whole document rides that one prompt.
        var singlePassPrompt = SummarizationPrompts.BuildSinglePass(text);
        var singlePassBudget = ComputeBudget(context, singlePassPrompt.Instructions);

        if (documentTokens <= singlePassBudget.PerCallTokens)
        {
            var single = await CallAsync(context, singlePassPrompt, cancellationToken)
                .ConfigureAwait(false);

            return BuildRun(context, single.Text, [single], mapUnitCount: 0, collapsePasses: 0);
        }

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

        _logger.LogInformation(
            "Summarizing an oversized document of {DocumentTokens} tokens as {UnitCount} map units "
                + "against a per-unit budget of {UnitBudget} tokens",
            documentTokens, units.Count, mapBudget.MapUnitTokens);

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
    /// <para>
    /// A unit already in flight when a sibling fails is left to finish, because the acceptance
    /// criterion says a failing unit must not cancel its siblings. That is the criterion's price,
    /// not a saving: the run fails regardless, so those results are discarded anyway, and letting
    /// them finish adds their output tokens and delays the failure by up to one call timeout.
    /// </para>
    /// <para>
    /// A unit that has not started yet is a different matter. The run fails once any unit exhausts
    /// its retries, so every call dispatched after that point is bought for a result nobody will
    /// read — on a large document that is hundreds of calls. Queued units are therefore skipped
    /// rather than cancelled, which is why the flag is checked after the gate rather than before it.
    /// </para>
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

        // Task.WhenAll preserves input order, so the partials line up with the units that produced
        // them. It also throws before this line whenever any unit failed, so nothing here is null.
        var completed = results.Select(r => r!.Value).ToList();

        return ([.. completed.Select(r => r.Text)], completed);
    }

    /// <remarks>
    /// What plain map-reduce lacks. Without it a document whose own partial summaries overflow the
    /// window has no answer at all — not a worse summary, no summary.
    /// </remarks>
    private async Task<(IReadOnlyList<string> Partials, int Passes, IReadOnlyList<CallResult> Calls)>
        RunCollapseLoopAsync(
            RunContext context,
            IReadOnlyList<string> mapped,
            CancellationToken cancellationToken)
    {
        var partials = mapped;
        List<CallResult> calls = [];
        var reduceBudget = ComputeBudget(
            context, SummarizationPrompts.BuildReduce(["x"]).Instructions);

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

            // Grouping can legitimately leave the count unchanged — when one partial summary
            // already fills the budget, nothing can be combined with it, and the pass still earns
            // its cost by compressing that partial on its own. Whether the partials shrink fast
            // enough is what the pass limit above answers; it is not knowable from the grouping.
            var groups = GroupToFit(context, partials, reduceBudget.PerCallTokens);

            _logger.LogInformation(
                "Collapse pass {Pass} reducing {PartialCount} partial summaries into {GroupCount}",
                passes, partials.Count, groups.Count);

            var collapsed = new List<CallResult>();

            foreach (var group in groups)
            {
                collapsed.Add(await CallAsync(
                    context, SummarizationPrompts.BuildReduce(group), cancellationToken)
                    .ConfigureAwait(false));
            }

            calls.AddRange(collapsed);
            partials = [.. collapsed.Select(c => c.Text)];
        }

        return (partials, passes, calls);
    }

    private List<List<string>> GroupToFit(RunContext context, IReadOnlyList<string> partials, long budget)
    {
        var groups = new List<List<string>>();
        var current = new List<string>();
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
                // Full jitter, so a document whose map units all fault at once does not retry them
                // in lockstep against a provider that is already struggling.
                var ceiling = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling.TotalMilliseconds);

                _logger.LogWarning(
                    ex,
                    "A summarization call failed on attempt {Attempt} of {MaxAttempts}; retrying in {Delay}",
                    attempt + 1, _options.MaxCallRetries + 1, delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <remarks>
    /// <para>
    /// Deliberately narrow, for two reasons. Retrying a bad credential, a repointed deployment name,
    /// a content-filter rejection or a prompt the provider says is too long cannot succeed, and the
    /// map phase multiplies that waste by the unit count.
    /// </para>
    /// <para>
    /// More importantly, this loop sits on top of one that has already run. Neither chat-client
    /// registration overrides the client pipeline's retry policy, so the provider SDK has already
    /// made its own attempts — honouring <c>Retry-After</c>, which this loop's blind jitter cannot —
    /// before any status-coded or transport failure reaches here. Retrying those again would mean
    /// several times the requests per map unit against a provider that is already shedding load. Only
    /// the two conditions the SDK's policy cannot see are retried: this call's own deadline, which
    /// belongs to us and not to it, and a completion that came back empty.
    /// </para>
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

        // The first call site in this codebase to put MaxOutputTokens on the wire for any provider.
        // A map phase can run hundreds of calls, and without a cap each one's output is unbounded at
        // the summarizer's rate. That Azure OpenAI honours it as a hard cap rather than a hint is an
        // accepted, unverified assumption — it has not been checked against a live deployment.
        // Guarded on a positive value because a catalog row nobody filled in would otherwise put a
        // cap of zero on the wire.
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
            // Only the caller's own token means cancellation. Everything else that cancels a call —
            // this call's deadline, or the provider SDK's own per-attempt network timeout, which
            // surfaces as a TaskCanceledException with nobody's token set — is a failure to respond
            // and has to be named as one. Left as an OperationCanceledException it would escape the
            // run's failure contract and be reported to the user as though they had given up.
            throw new TimeoutException(
                $"The summarizer did not respond within {_options.CallTimeoutSeconds} seconds.", ex);
        }

        var text = response.Messages.Count > 0
            ? response.Messages[^1].Text?.Trim() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            throw new EmptyCompletionException();
        }

        // Preferred over ChatResponse.Usage for the reason the naming path prefers it: the report
        // separates the assistant's own consumption from anything a tool spent. Summarization
        // attaches no tools, so the two agree today, and the raw response is the fallback for a
        // client that has no tracking installed.
        var report = response.AdditionalProperties?.TryGetValue(
            ToolTrackingChatClient.UsageReportPropertyName, out ChatUsageReport? usageReport) is true
                ? usageReport
                : null;

        return new CallResult(
            text,
            report?.AssistantUsage.InputTokenCount ?? response.Usage?.InputTokenCount ?? 0,
            report?.AssistantUsage.OutputTokenCount ?? response.Usage?.OutputTokenCount ?? 0);
    }

    private SummarizationBudget ComputeBudget(RunContext context, string instructions) =>
        SummarizationBudgetCalculator.Compute(
            context.Summarizer,
            _promptOverheadCalculator.Estimate(
                messageCount: 1, toolCount: 0, CountTokens(context, instructions)),
            _options.SafetyFraction,
            _options.MapUnitFraction);

    /// <remarks>
    /// Guarded for the reason the turn path guards its own counting: a tokenizer fault while sizing
    /// a call must not fail a run. Counting low routes an oversized document down the single-pass
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
            _logger.LogWarning(ex, "Token estimation failed while sizing a summarization call");

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
            context.Summarizer.ModelId,
            context.Summarizer.DeploymentName,
            SummarizationPrompts.PromptVersion);

    /// <remarks>
    /// A <see cref="ValidationException"/> specifically: it is the one type the background job
    /// processor passes through verbatim to a job's error message, so a reason written here is the
    /// reason a user eventually reads.
    /// </remarks>
    private static ValidationException Invalid(string property, string message) =>
        new([new ValidationFailure(property, message)]);

    #endregion

    private sealed record RunContext(
        SummarizerModel Summarizer,
        ITokenEstimator Estimator,
        IChatClient ChatClient);

    private readonly record struct CallResult(string Text, long InputTokens, long OutputTokens);
}
