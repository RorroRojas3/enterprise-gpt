using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tool;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;

namespace Enterprise.Gpt.Service.Summarization;

/// <summary>
/// One document's summary, and whether producing it cost anything.
/// </summary>
/// <param name="Text">The summary text.</param>
/// <param name="FromCache">
/// <see langword="true"/> when the stored summary was reused, in which case no model call was made
/// and no usage was billed.
/// </param>
/// <param name="ModelCallCount">Model calls this request made: <c>0</c> on a cache hit.</param>
public sealed record DocumentSummaryResult(string Text, bool FromCache, int ModelCallCount);

/// <summary>
/// Serves a document's canonical summary, generating and billing it on a miss.
/// </summary>
/// <remarks>
/// The only caller of <see cref="IDocumentSummarizer"/>. The engine below this seam knows how to
/// summarize text and nothing about caching, ownership or money; everything on this side of it is
/// caching, ownership and money.
/// </remarks>
public interface IDocumentSummaryService
{
    /// <summary>
    /// Returns a document's summary, generating it if none is stored.
    /// </summary>
    /// <param name="conversationId">The conversation to bill any model calls to.</param>
    /// <param name="documentId">The document to summarize.</param>
    /// <param name="source">Which document family <paramref name="documentId"/> belongs to.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The summary, and whether it came from the cache.</returns>
    /// <exception cref="ValidationException">The document has no text, or the run failed.</exception>
    /// <exception cref="SummarizerNotConfiguredException">
    /// The summarizer's catalog row is missing or unusable.
    /// </exception>
    Task<DocumentSummaryResult> GetOrCreateAsync(
        Guid conversationId, Guid documentId, DocumentSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Reduces every document in a conversation's retrieval scope to one digest.
    /// </summary>
    /// <param name="conversationId">The conversation to bill to, and whose scope is digested.</param>
    /// <param name="scope">
    /// The documents in scope, from <see cref="IDocumentRetrievalService.GetScopeAsync"/>.
    /// </param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The digest text. Never persisted — a later request recomputes it.</returns>
    /// <remarks>
    /// Documents that already have a summary contribute at no cost, so a five-document conversation
    /// with four already summarized costs one run plus the digest, not five.
    /// </remarks>
    /// <exception cref="ValidationException">The scope is empty or exceeds the configured maximum.</exception>
    Task<string> CreateDigestAsync(
        Guid conversationId, DocumentRetrievalScope scope, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class DocumentSummaryService(
    IDocumentSummarizer summarizer,
    EnterpriseGptDbContext ctx,
    IServiceScopeFactory scopeFactory,
    IOptions<SummarizationOptions> options,
    ILogger<DocumentSummaryService> logger) : IDocumentSummaryService
{
    private readonly IDocumentSummarizer _summarizer = summarizer;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly SummarizationOptions _options = options.Value;
    private readonly ILogger<DocumentSummaryService> _logger = logger;

    /// <inheritdoc />
    public async Task<DocumentSummaryResult> GetOrCreateAsync(
        Guid conversationId, Guid documentId, DocumentSource source, CancellationToken cancellationToken)
    {
        var cached = await ReadCachedTextAsync(documentId, source, cancellationToken).ConfigureAwait(false);

        if (cached is not null)
        {
            // Recorded so the cache's hit rate sits in the same series as its misses. A hit has no
            // deployment of its own because nothing served it.
            ChatMetrics.RecordSummaryRun(
                deploymentName: null, path: "cache-hit", outcome: "success", TimeSpan.Zero, mapUnits: null);

            return new DocumentSummaryResult(cached, FromCache: true, ModelCallCount: 0);
        }

        var run = await MeasureAsync(
            () => _summarizer.SummarizeDocumentAsync(documentId, source, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // Uncancellable on purpose, for the reason the turn's own finalization is
        // (ConversationService.PersistTurnAsync): by this line the run has already been paid for at
        // the provider, and the token that would abort the write is the same one a client
        // disconnect or the tool deadline signals. Losing the race would spend the money and record
        // neither the summary nor the usage row.
        await PersistAsync(conversationId, documentId, source, run, CancellationToken.None).ConfigureAwait(false);

        return new DocumentSummaryResult(run.Summary, FromCache: false, run.ModelCallCount);
    }

    /// <inheritdoc />
    public async Task<string> CreateDigestAsync(
        Guid conversationId, DocumentRetrievalScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!scope.HasDocuments)
        {
            throw Invalid("summary", "This conversation has no documents to summarize.");
        }

        if (scope.Documents.Count > _options.MaxDigestDocuments)
        {
            throw Invalid(
                "summary",
                $"This conversation has {scope.Documents.Count} documents and at most "
                    + $"{_options.MaxDigestDocuments} can be summarized together. Summarize individual "
                    + "documents instead.");
        }

        // Sequential rather than fanned out. Each miss runs its own bounded-concurrency map phase
        // inside the engine, and overlapping several of those would multiply the in-flight call
        // count by the document count against a gate sized for one document.
        var parts = new StringBuilder();

        for (var index = 0; index < scope.Documents.Count; index++)
        {
            var document = scope.Documents[index];

            var summary = await GetOrCreateAsync(
                conversationId, document.Id, document.Source, cancellationToken).ConfigureAwait(false);

            parts.Append("Document ").Append(index + 1).Append(" of ").Append(scope.Documents.Count)
                .Append(": ").AppendLine(document.Name)
                .AppendLine(summary.Text)
                .AppendLine();
        }

        var run = await MeasureAsync(
            () => _summarizer.SummarizeDigestAsync(parts.ToString(), cancellationToken),
            cancellationToken,
            path: "digest").ConfigureAwait(false);

        // Billed like any other run and deliberately not persisted: a digest describes a scope, and
        // the scope changes the moment a document is added to or removed from the conversation.
        // Uncancellable for the same reason the document path's write is.
        await BillAsync(conversationId, run, CancellationToken.None).ConfigureAwait(false);

        return run.Summary;
    }

    /// <remarks>
    /// Filtered on the running prompt version, which is the whole cache-invalidation mechanism: a
    /// document's text never changes after ingestion, so a stored summary can only go stale when
    /// the prompts that produced it change. Without this the feature would need a regenerate flag,
    /// and a regenerate flag the model can set is a way for the model to spend money.
    /// </remarks>
    private async Task<string?> ReadCachedTextAsync(
        Guid documentId, DocumentSource source, CancellationToken cancellationToken)
    {
        var version = SummarizationPrompts.PromptVersion;

        return source switch
        {
            DocumentSource.Conversation => await _ctx.ConversationDocumentSummaries
                .AsNoTracking()
                .Where(x => x.ConversationDocumentId == documentId
                    && !x.DateDeactivated.HasValue
                    && x.PromptVersion == version)
                .Select(x => x.Text)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false),

            DocumentSource.Project => await _ctx.ProjectDocumentSummaries
                .AsNoTracking()
                .Where(x => x.ProjectDocumentId == documentId
                    && !x.DateDeactivated.HasValue
                    && x.PromptVersion == version)
                .Select(x => x.Text)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown document source.")
        };
    }

    /// <summary>
    /// Runs the engine detached from any ambient tool scope, then times it and records its outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The detach is load-bearing, not defensive. <see cref="IDocumentSummarizer"/> resolves its
    /// client through the same <see cref="Chat.IChatClientResolver"/> the turn does, so its calls
    /// run on the fully decorated pipeline — tool tracking included — and when this is reached from
    /// inside a tool invocation the tracker attributes them to the enclosing scope. Those tokens
    /// then land in the turn's own <c>ConversationUsage.ToolInputTokens</c>, where
    /// <c>EstimatedCost</c> prices them at the <em>driving</em> model's rate and
    /// <c>ConversationUsageToolCall.ModelId</c> is null — so a run would be billed as if the
    /// conversation's own chat model had made it, and billed a second time by the row written
    /// below. Detaching is what makes the summarizer's cost the summarizer's alone.
    /// </para>
    /// <para>
    /// The tracker's ambient scope is an <see cref="System.Threading.AsyncLocal{T}"/> it keeps
    /// internal, so the only way to leave it is to run on an execution context that never inherited
    /// it — hence the suppressed flow. <see cref="Activity.Current"/> is carried across by hand so
    /// the summarizer's own spans still hang off the turn's trace: the aim is to detach from the
    /// usage tracker, not from telemetry.
    /// </para>
    /// <para>
    /// Only the engine's own calls are detached. Whatever the caller reports through
    /// <see cref="Andes.Extensions.AI.ChatProgress"/> stays attributed, because that is what draws
    /// the activity card the user is watching.
    /// </para>
    /// <para>
    /// The path tag is read off the finished run rather than predicted, so a document that turned
    /// out to need map-reduce is not filed under the single pass it was hoped to be.
    /// </para>
    /// </remarks>
    private static async Task<SummarizationRun> MeasureAsync(
        Func<Task<SummarizationRun>> run, CancellationToken cancellationToken, string? path = null)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await RunDetachedAsync(run).ConfigureAwait(false);

            ChatMetrics.RecordSummaryRun(
                result.DeploymentName,
                path ?? (result.MapUnitCount == 0 ? "single-pass" : "map-reduce"),
                outcome: result.Truncated ? "truncated" : "success",
                Stopwatch.GetElapsedTime(started),
                result.MapUnitCount);

            return result;
        }
        catch (Exception ex)
        {
            ChatMetrics.RecordSummaryRun(
                deploymentName: null,
                path ?? "unknown",
                outcome: ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? "cancelled"
                    : "failure",
                Stopwatch.GetElapsedTime(started),
                mapUnits: null);

            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="run"/> on an execution context that never inherited the caller's, so no
    /// <see cref="System.Threading.AsyncLocal{T}"/> the tool tracker set is visible inside it.
    /// </summary>
    private static async Task<SummarizationRun> RunDetachedAsync(Func<Task<SummarizationRun>> run)
    {
        var parent = Activity.Current;

        Task<SummarizationRun> detached;

        using (ExecutionContext.SuppressFlow())
        {
            detached = Task.Run(() =>
            {
                Activity.Current = parent;

                return run();
            });
        }

        return await detached.ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the summary row and its usage row together, on a unit of work of its own.
    /// </summary>
    /// <remarks>
    /// A fresh scope rather than the request's shared <see cref="EnterpriseGptDbContext"/>, which is
    /// the same instance <c>ConversationService.PersistTurnAsync</c> saves the turn's own audit and
    /// transcript through. EF accepts changes only on a <em>successful</em> save, so a failure here —
    /// the filtered unique index rejecting a concurrent identical insert, a rowversion conflict
    /// between two turns regenerating the same row, a transient fault — would leave the summary and
    /// usage rows tracked as <c>Added</c> on the turn's context. The tool turns that failure into a
    /// sentence for the model and the turn carries on, so the turn's own save would then re-attempt
    /// those inserts: either failing the turn's finalization after a fully streamed answer, or
    /// committing a second usage row and double-billing the run. Its own scope makes the two writes
    /// independent, and keeps a mid-stream transaction off the connection the turn transacts on later.
    /// </remarks>
    /// <remarks>
    /// A second connection makes one ordering fact load-bearing that nothing asserts: because
    /// <see cref="BillCoreAsync"/> takes an exclusive lock on the conversation row, an uncommitted
    /// transaction held on the request's own context while the tool runs would block this write until
    /// command timeout — and since the request is awaiting the tool, it could never resolve itself.
    /// It is safe today only because no transaction is open on that context for the life of the
    /// stream: conversation naming commits inside its own method before the turn's options are built,
    /// and the turn's own transaction opens after the stream drains. A change that holds one across
    /// the stream would deadlock here rather than fail.
    /// </remarks>
    private async Task PersistAsync(
        Guid conversationId,
        Guid documentId,
        DocumentSource source,
        SummarizationRun run,
        CancellationToken cancellationToken)
    {
        var date = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await using var transaction = await ctx.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Rewritten in place rather than appended to. The unique index is filtered on
        // DateDeactivated, so inserting beside a live row of a superseded prompt version would be a
        // constraint violation rather than a second version — and version history is not what a
        // summary is.
        var existing = await ReadLiveRowAsync(ctx, documentId, source, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            BaseDocumentSummary created = source switch
            {
                DocumentSource.Conversation => new ConversationDocumentSummary { ConversationDocumentId = documentId },
                DocumentSource.Project => new ProjectDocumentSummary { ProjectDocumentId = documentId },
                _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown document source.")
            };

            created.Id = Guid.NewGuid();
            created.DateCreated = date;
            Apply(created, run, date);

            ctx.Add(created);
        }
        else
        {
            Apply(existing, run, date);
        }

        await BillCoreAsync(ctx, conversationId, run, date, cancellationToken).ConfigureAwait(false);

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Summarized document {DocumentId} ({Source}) for conversation {ConversationId} in {CallCount} call(s), "
                + "{MapUnitCount} map unit(s), {CollapsePasses} collapse pass(es)",
            documentId, source, conversationId, run.ModelCallCount, run.MapUnitCount, run.CollapsePasses);
    }

    /// <summary>
    /// Writes only the usage row, for a run whose output is not stored. On its own unit of work for
    /// the reason <see cref="PersistAsync"/> is.
    /// </summary>
    private async Task BillAsync(Guid conversationId, SummarizationRun run, CancellationToken cancellationToken)
    {
        var date = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await using var transaction = await ctx.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await BillCoreAsync(ctx, conversationId, run, date, cancellationToken).ConfigureAwait(false);

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bumps the conversation's counters and stages the usage row. The caller owns the transaction.
    /// </summary>
    /// <remarks>
    /// One row per run, matching how a chat turn is recorded: a turn's several provider round trips
    /// are one row, not one each, with the breakdown kept elsewhere. The row carries the
    /// summarizer's own model, provider, deployment and prices, which is the entire point of
    /// pinning a summarizer — a run requested from a conversation whose selected chat model is an
    /// expensive reasoning deployment must not be priced at that deployment's rate.
    /// </remarks>
    private static async Task BillCoreAsync(
        EnterpriseGptDbContext ctx,
        Guid conversationId,
        SummarizationRun run,
        DateTimeOffset date,
        CancellationToken cancellationToken)
    {
        // Read against run.ModelId rather than resolved from configuration a second time, so the
        // provider and prices on the row cannot disagree with the model the run snapshotted.
        var model = await ctx.Models
            .AsNoTracking()
            .Where(x => x.Id == run.ModelId)
            .Select(x => new
            {
                x.ProviderId,
                x.InputPricePerMillionTokens,
                x.OutputPricePerMillionTokens
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SummarizerNotConfiguredException(run.ModelId);

        var owner = await ctx.Conversations
            .AsNoTracking()
            .Where(x => x.Id == conversationId)
            .Select(x => (Guid?)x.UserId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Invalid("summary", "The conversation this summary was requested from no longer exists.");

        await ctx.Conversations
            .Where(x => x.Id == conversationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.InputTokens, x => x.InputTokens + run.InputTokens)
                .SetProperty(x => x.OutputTokens, x => x.OutputTokens + run.OutputTokens),
                cancellationToken)
            .ConfigureAwait(false);

        ctx.Add(new ConversationUsage
        {
            ConversationId = conversationId,
            UserId = owner,
            ModelId = run.ModelId,
            ProviderId = model.ProviderId,
            DeploymentName = run.DeploymentName,
            Kind = ConversationUsageKinds.Summarization,
            Status = ConversationUsageStatuses.Completed,
            InputTokens = run.InputTokens,
            OutputTokens = run.OutputTokens,
            // Null deliberately: a summarization run is billed and never transcribed, so there is
            // no transcript message to estimate a context cost from. Null says "not transcribed"
            // where zero would say "transcribed nothing".
            ContextTokens = null,
            InputPricePerMillionTokens = model.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = model.OutputPricePerMillionTokens,
            DateCreated = date
        });
    }

    private static async Task<BaseDocumentSummary?> ReadLiveRowAsync(
        EnterpriseGptDbContext ctx,
        Guid documentId,
        DocumentSource source,
        CancellationToken cancellationToken) => source switch
        {
            DocumentSource.Conversation => await ctx.ConversationDocumentSummaries
                .Where(x => x.ConversationDocumentId == documentId && !x.DateDeactivated.HasValue)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false),

            DocumentSource.Project => await ctx.ProjectDocumentSummaries
                .Where(x => x.ProjectDocumentId == documentId && !x.DateDeactivated.HasValue)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown document source.")
        };

    private static void Apply(BaseDocumentSummary summary, SummarizationRun run, DateTimeOffset date)
    {
        summary.Text = run.Summary;
        summary.ModelId = run.ModelId;
        summary.DeploymentName = run.DeploymentName;
        summary.PromptVersion = run.PromptVersion;
        summary.InputTokens = run.InputTokens;
        summary.OutputTokens = run.OutputTokens;
        summary.ModelCallCount = run.ModelCallCount;
        summary.MapUnitCount = run.MapUnitCount;
        summary.CollapsePasses = run.CollapsePasses;
        summary.DateModified = date;
    }

    /// <summary>
    /// The one exception type the tool relays to the model verbatim, matching the engine's own
    /// failure contract.
    /// </summary>
    private static ValidationException Invalid(string property, string message) =>
        new([new ValidationFailure(property, message)]);
}
