using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Conversations;
using Enterprise.Gpt.Dto.ChatModels;
using Enterprise.Gpt.Dto.Conversations;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Entity.Transcripts.Models;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Repository.Cosmos;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Agents.Constants;
using Enterprise.Gpt.Service.Agents.Models;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.ChatModels;
using Enterprise.Gpt.Service.Conversations.Chat;
using Enterprise.Gpt.Service.Conversations.Exceptions;
using Enterprise.Gpt.Service.Conversations.Rendering;
using Enterprise.Gpt.Service.Conversations.Tokenization;
using Enterprise.Gpt.Service.Conversations.Tokenization.Models;
using Enterprise.Gpt.Service.Documents;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Enterprise.Gpt.Service.Documents.Retrieval.Models;
using Enterprise.Gpt.Service.Documents.Sheets;
using Enterprise.Gpt.Service.Documents.Summarization;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.McpServers;
using Enterprise.Gpt.Service.McpServers.Models;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Security;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Sorting.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Conversation = Enterprise.Gpt.Entity.Conversations.Conversation;

namespace Enterprise.Gpt.Service.Conversations;

public interface IConversationService
{
    /// <summary>
    /// The <c>metadata</c> key the <see cref="AssistantUiEventKind.Finished"/> frame carries the
    /// persisted assistant message's identifier under.
    /// </summary>
    /// <remarks>
    /// Clients match it verbatim, so renaming it is a breaking API change. Absent unless the answer
    /// reached the transcript.
    /// </remarks>
    const string AssistantMessageIdMetadataKey = "assistantMessageId";

    /// <summary>
    /// The <c>metadata</c> key the <see cref="AssistantUiEventKind.Finished"/> frame carries the
    /// files this turn produced under, as a JSON array of
    /// <see cref="MessageAttachmentDto"/>.
    /// </summary>
    /// <remarks>
    /// Clients match it verbatim. JSON in a string because the vendored contract's metadata bag is
    /// <c>Record&lt;string, string&gt;</c>; absent when the turn produced no file.
    /// </remarks>
    const string GeneratedAttachmentsMetadataKey = "generatedAttachments";

    /// <summary>Reads a conversation with the model and MCP servers its last turn ran with.</summary>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    Task<ConversationDetailDto> GetConversationAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists the active documents of one of the caller's conversations, newest first.</summary>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    Task<List<ConversationDocumentDto>> GetConversationDocumentsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ConversationDto> CreateConversationAsync(CreateConversationActionDto request, CancellationToken cancellationToken = default);

    Task DeactivateConversationAsync(Guid id, CancellationToken cancellationToken = default);

    Task DeactivateConversationsBulkAsync(DeactivateConversationsBulkActionDto request, CancellationToken cancellationToken = default);

    Task UpdateConversationNameAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the caller's active conversations, newest first unless an order is requested.
    /// </summary>
    /// <param name="name">A name substring, or <see langword="null"/> for no filter.</param>
    /// <param name="skip">Conversations to skip.</param>
    /// <param name="take">The page size.</param>
    /// <param name="isFavorite">Favourites, non-favourites, or <see langword="null"/> for both.</param>
    /// <param name="projectId">One of the caller's projects, or <see langword="null"/> for all.</param>
    /// <param name="sort">One of <see cref="ConversationSortKeyNames.Supported"/>.</param>
    /// <param name="dir"><c>asc</c> or <c>desc</c>.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="NotFoundException"><paramref name="projectId"/> is not an active project of the caller.</exception>
    /// <exception cref="ValidationException">The sort field or direction is not accepted.</exception>
    Task<PaginatedResponseDto<ConversationDto>> SearchConversationsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, Guid? projectId = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default);

    Task<ConversationDto> UpdateConversationAsync(UpdateConversationActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the caller's conversation as a favourite, or clears the mark.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    Task SetConversationFavoriteAsync(Guid id, SetConversationFavoriteActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one chat turn and streams what the assistant does while it runs: answer text,
    /// reasoning, and an activity event for every tool, MCP call and agent it invokes.
    /// </summary>
    /// <remarks>
    /// Validation and identity resolve eagerly, so a bad request faults before streaming; contention and
    /// a missing conversation surface on the first move.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ConversationBusyException">Another turn is already in flight for this conversation.</exception>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    IAsyncEnumerable<AssistantUiEvent> StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a conversation's transcript, oldest message first, excluding system messages.
    /// </summary>
    /// <param name="id">The conversation.</param>
    /// <param name="take">
    /// How many of the newest messages to return, clamped to 1–100; omitted returns the whole transcript,
    /// which keeps pre-paging clients working.
    /// </param>
    /// <param name="before">Only messages written strictly before this moment, for paging backwards.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    Task<ChatConversationDto> GetConversationMessagesAsync(
        Guid id, int? take, DateTimeOffset? before, CancellationToken cancellationToken);

    /// <summary>
    /// Records, replaces or withdraws (null rating) the caller's rating of an assistant message.
    /// </summary>
    /// <remarks>
    /// The transcript document is the source of truth; the relational <c>MessageFeedback</c> row written
    /// after it is a reporting projection whose failure is logged, not thrown.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotFoundException">
    /// The conversation is not an active conversation of the caller, or holds no such message.
    /// </exception>
    /// <exception cref="ValidationException">The message is not an assistant message.</exception>
    Task SetMessageFeedbackAsync(
        Guid conversationId, Guid messageId, SetMessageFeedbackActionDto request,
        CancellationToken cancellationToken = default);
}

public partial class ConversationService(ILogger<ConversationService> logger,
    IModelService modelService,
    IChatClientResolver chatClientResolver,
    IMcpToolProvider mcpToolProvider,
    IDocumentRetrievalService documentRetrievalService,
    IConversationLockService conversationLockService,
    ITokenService tokenService,
    ITranscriptStore transcriptStore,
    ITokenEstimatorResolver tokenEstimatorResolver,
    PromptOverheadCalculator promptOverheadCalculator,
    IMarkdownRenderer markdownRenderer,
    IDocumentSummaryService documentSummaryService,
    IUserGrantReader userGrantReader,
    IOptions<SummarizationOptions> summarizationOptions,
    ISheetQueryService sheetQueryService,
    ISheetQueryOptionsProvider sheetQueryOptions,
    IFileAgentToolProvider fileAgentToolProvider,
    IFileAgentQuotaService fileAgentQuotaService,
    IOptions<FileAgentOptions> fileAgentOptions,
    EnterpriseGptDbContext ctx,
    TimeProvider timeProvider) : IConversationService
{
    private readonly ILogger _logger = logger;
    private readonly IChatClientResolver _chatClientResolver = chatClientResolver;
    private readonly IModelService _modelService = modelService;
    private readonly IMcpToolProvider _mcpToolProvider = mcpToolProvider;
    private readonly IDocumentRetrievalService _documentRetrievalService = documentRetrievalService;
    private readonly IConversationLockService _conversationLockService = conversationLockService;
    private readonly ITokenService _tokenService = tokenService;
    private readonly ITranscriptStore _transcriptStore = transcriptStore;
    private readonly ITokenEstimatorResolver _tokenEstimatorResolver = tokenEstimatorResolver;
    private readonly PromptOverheadCalculator _promptOverheadCalculator = promptOverheadCalculator;
    private readonly IMarkdownRenderer _markdownRenderer = markdownRenderer;

    // Once per model: an unset context window is permanent, and a per-turn warning would bury the log.
    private readonly ConcurrentDictionary<Guid, bool> _unboundedModelsReported = new();
    private readonly IDocumentSummaryService _documentSummaryService = documentSummaryService;
    private readonly IUserGrantReader _userGrantReader = userGrantReader;
    private readonly SummarizationOptions _summarizationOptions = summarizationOptions.Value;
    private readonly ISheetQueryService _sheetQueryService = sheetQueryService;
    private readonly SheetQueryOptions _sheetQueryOptions = sheetQueryOptions.Current;
    private readonly IFileAgentToolProvider _fileAgentToolProvider = fileAgentToolProvider;
    private readonly IFileAgentQuotaService _fileAgentQuotaService = fileAgentQuotaService;
    private readonly FileAgentOptions _fileAgentOptions = fileAgentOptions.Value;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly TimeProvider _timeProvider = timeProvider;

    // The synthetic pair every stream opens with. The trailing blank line stops the reducer, which
    // accumulates reasoning verbatim, running a model's first real delta on from this sentence.
    private const string StartingStatusMessage = "Starting";
    private const string PreparingReasoningText = "Reviewing the request and preparing a response.\n\n";

    /// <inheritdoc />
    public async Task<ConversationDetailDto> GetConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();

        // Projected inline rather than through MapToChatDto: that mapper runs client-side in a
        // top-level projection, so the whole entity would be materialized to build the DTO.
        var conversation = await _ctx.Conversations
            .AsNoTracking()
            .Where(s => s.Id == id && s.UserId == userId)
            .Select(s => new ConversationDetailDto
            {
                Id = s.Id,
                ProjectId = s.ProjectId,
                ModelId = s.ModelId,
                Name = s.Name,
                IsFavorite = s.IsFavorite,
                DateCreated = s.DateCreated,
                DateModified = s.DateModified
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            LogConversationNotFound(_logger, id);
            throw new NotFoundException($"Conversation with id {id} not found");
        }

        // The newest chat turn's servers (naming calls carry none), narrowed to what
        // McpToolProvider.AcquireToolsAsync accepts so the client never replays one into a 404 or 403.
        // A second round trip: the correlated form translates to SQL APPLY, which not every provider has.
        conversation.McpServerIds = await _ctx.ConversationUsage
            .AsNoTracking()
            .Where(u => u.ConversationId == id && u.Kind == ConversationUsageKinds.Chat)
            .OrderByDescending(u => u.DateCreated)
            .Take(1)
            .SelectMany(u => u.McpServers
                .Where(m => !m.McpServer.DateDeactivated.HasValue
                    && m.McpServer.Permissions.Any(p => p.UserPermissions.Any(up => up.UserId == userId)))
                .Select(m => m.McpServerId))
            .ToListAsync(cancellationToken);

        return conversation;
    }

    /// <inheritdoc />
    public async Task<List<ConversationDocumentDto>> GetConversationDocumentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();
        await EnsureConversationExistsAsync(id, userId, cancellationToken);

        // Uploads only: a file the assistant made reaches the user through its message's chip instead.
        return await _ctx.ConversationDocuments
            .AsNoTracking()
            .Where(x => x.ConversationId == id
                && x.Type == ConversationDocumentTypes.Uploaded)
            .OrderByDescending(x => x.DateCreated)
            .Select(ConversationDocumentMapper.MapToConversationDocumentDtoExpression)
            .ToListAsync(cancellationToken);
    }

    public async Task<ConversationDto> CreateConversationAsync(CreateConversationActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();

        if (request.ProjectId.HasValue)
        {
            await EnsureProjectExistsAsync(request.ProjectId.Value, userId, cancellationToken);
        }

        // The Cosmos write is inside the transaction so a failure there rolls the relational row
        // back rather than leaving a conversation the user can open but never stream.
        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);
        var newChat = new Conversation()
        {
            Name = "New Conversation",
            UserId = userId,
            ProjectId = request.ProjectId
        };

        // Add assigns the client-generated key, which the seeded message needs before the save.
        _ctx.Add(newChat);

        // No model is chosen yet, so the uncalibrated default is the honest estimator; the first turn
        // re-estimates anyway.
        var systemMessage = BuildSeededSystemMessage(
            userId, newChat.Id, default, _tokenEstimatorResolver.Default, deploymentName: null);

        // Both counters start from the same message; later turns move them by the same delta, so a
        // gap seeded here would never close.
        newChat.ContextTokens = systemMessage.Tokens;
        await _ctx.SaveChangesAsync(cancellationToken);

        // The transcript takes the relational row's stamp, so both stores agree on when it began.
        var date = newChat.DateCreated;
        systemMessage = systemMessage with { DateCreated = date };

        var header = TranscriptHeaderDocument.Create(userId, newChat.Id, newChat.Name, date) with
        {
            MessageCount = 1,
            ContextTokens = systemMessage.Tokens
        };

        // Header and seeded message in one batch: a header whose counters claim a message that
        // was never written is exactly the drift the per-message shape exists to remove.
        var created = await _transcriptStore.ExecuteBatchAsync(
            TranscriptPartition(userId, newChat.Id),
            batch => batch.Create(header.Id, header).Create(systemMessage.Id, systemMessage),
            cancellationToken);

        if (!created.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Creating the transcript for conversation {newChat.Id} failed: {created.Describe()}.");
        }

        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The rollback cannot undo the Cosmos documents, so a failed commit orphans a transcript
            // partition; its key is logged because no query can find it afterwards.
            LogConversationCommitFailed(_logger, ex, newChat.Id, PartitionKeys.For(userId, newChat.Id));

            throw;
        }

        return newChat.MapToChatDto();
    }

    public async Task DeactivateConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();
        var date = _timeProvider.GetUtcNow();

        var conversationExists = await _ctx.Conversations
            .Where(x => x.Id == id && x.UserId == userId)
            .AnyAsync(cancellationToken);

        if (!conversationExists)
        {
            LogConversationNotFoundOrDeactivated(_logger, id);
            return;
        }

        // Patched, not replaced, so a concurrently streaming turn's messages are not reverted. An
        // absent document is tolerated.
        await _transcriptStore.PatchItemAsync(id.ToString(), TranscriptPartition(userId, id),
        [
            PatchOperation.Set("/dateDeactivated", date),
        ], cancellationToken);

        // Summaries first, so one never outlives its document: a live summary under a deleted
        // document is still a readable account of it.
        await _ctx.ConversationDocumentSummaries
            .Where(s => s.ConversationDocument.ConversationId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        // Leaf first for the reason the summary goes first: a live cell row under a dead sheet is
        // the document's own data, still readable.
        await _ctx.ConversationDocumentSheetRows
            .Where(r => r.ConversationDocumentSheet.ConversationDocument.ConversationId == id)
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheetColumns
            .Where(c => c.ConversationDocumentSheet.ConversationDocument.ConversationId == id)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheets
            .Where(s => s.ConversationDocument.ConversationId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentChunks
            .Where(c => c.ConversationDocument.ConversationId == id)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocuments
            .Where(d => d.ConversationId == id)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.Conversations
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);
    }

    public async Task DeactivateConversationsBulkAsync(DeactivateConversationsBulkActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();
        var date = _timeProvider.GetUtcNow();

        var conversationIds = await _ctx.Conversations
            .Where(x => request.ConversationIds.Contains(x.Id) && x.UserId == userId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (conversationIds.Count == 0)
        {
            LogNoValidConversationsFound(_logger);
            return;
        }

        // Patched by id, with no read-modify-write window; SQL has already narrowed the ids to this user's.
        foreach (var conversationId in conversationIds)
        {
            await _transcriptStore.PatchItemAsync(
                conversationId.ToString(), TranscriptPartition(userId, conversationId),
            [
                PatchOperation.Set("/dateDeactivated", date),
            ], cancellationToken);
        }

        await _ctx.ConversationDocumentSummaries
            .Where(s => conversationIds.Contains(s.ConversationDocument.ConversationId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheetRows
            .Where(r => conversationIds.Contains(r.ConversationDocumentSheet.ConversationDocument.ConversationId))
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheetColumns
            .Where(c => conversationIds.Contains(c.ConversationDocumentSheet.ConversationDocument.ConversationId))
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheets
            .Where(s => conversationIds.Contains(s.ConversationDocument.ConversationId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentChunks
            .Where(c => conversationIds.Contains(c.ConversationDocument.ConversationId))
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocuments
            .Where(d => conversationIds.Contains(d.ConversationId))
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.Conversations
            .Where(s => conversationIds.Contains(s.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);
    }

    public async Task UpdateConversationNameAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Untracked so a later SaveChanges cannot write stale counters over the ExecuteUpdate below.
        // Owner-scoped although the only caller already checked: the method is public.
        var callerId = _tokenService.GetOid();
        var conversation = await _ctx.Conversations
            .AsNoTracking()
            .Where(x => x.Id == id && x.UserId == callerId)
            .Select(x => new { x.Id, x.UserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            LogConversationNotFound(_logger, id);
            throw new NotFoundException($"Conversation with id {id} not found");
        }

        var header = await ReadHeaderAsync(conversation.UserId, id, cancellationToken);
        if (header is null)
        {
            LogConversationHeaderNotFound(_logger, id);
            throw new NotFoundException($"Conversation for id {id} not found");
        }

        var model = await _ctx.Models
                    .AsNoTracking()
                    .Where(x => x.Id == request.ModelId && !x.DateDeactivated.HasValue)
                    .Select(x => new
                    {
                        x.DeploymentName,
                        x.ProviderId,
                        x.InputPricePerMillionTokens,
                        x.OutputPricePerMillionTokens
                    })
                    .FirstOrDefaultAsync(cancellationToken);
        // DeploymentName is not checked: the model validators enforce NotEmpty, and this path runs
        // on the first turn only.
        if (model is null)
        {
            LogModelNotFound(_logger, request.ModelId);
            throw new ValidationException(
            [
                new ValidationFailure(
                    nameof(CreateConversationStreamActionDto.ModelId),
                    $"Model with id {request.ModelId} not found")
            ]);
        }

        // Naming runs inline on the first turn, so it must use the turn's provider or fail the stream
        // before its first token.
        var chatClient = _chatClientResolver.Resolve(model.ProviderId);

        var response = await chatClient.GetResponseAsync([
                             new ChatMessage(ChatRole.System, ConversationPrompts.BuildNamingPrompt()),
                             new ChatMessage(ChatRole.User, $"Create a conversation name based on the following prompt, please make it 25 maximum and make it a string. Do not have the name on the conversation nor the id. Just the name based on the prompt. The result must be a string, not markdown. Prompt: {request.Prompt}")
                         ], new() { ModelId = model.DeploymentName }, cancellationToken);
        if (response is null)
        {
            LogConversationNamingFailed(_logger, id);
            throw new InvalidOperationException($"Failed to create name for id {id}");
        }

        var name = response.Messages.Last().Text?.Trim() ?? string.Empty;
        var date = _timeProvider.GetUtcNow();

        // A non-streamed tracked request carries its report on the response; ChatResponse.Usage is
        // the fallback when tracking is not installed.
        var report = response.AdditionalProperties?.TryGetValue(
            ToolTrackingChatClient.UsageReportPropertyName, out ChatUsageReport? usageReport) is true
                ? usageReport
                : null;

        var inputTokens = report?.AssistantUsage.InputTokenCount ?? response.Usage?.InputTokenCount ?? 0;
        var outputTokens = report?.AssistantUsage.OutputTokenCount ?? response.Usage?.OutputTokenCount ?? 0;

        // From the raw response, unlike the two above: the middleware's aggregation drops these, and
        // a non-streamed call has no per-turn breakdown to sum.
        var cachedInputTokens = response.Usage?.CachedInputTokenCount;
        var reasoningTokens = response.Usage?.ReasoningTokenCount;

        // Name, counters and usage row commit together, so totals that include the naming call
        // always carry the row that accounts for it.
        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        await _ctx.Conversations
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, name)
                .SetProperty(x => x.InputTokens, x => x.InputTokens + inputTokens)
                .SetProperty(x => x.OutputTokens, x => x.OutputTokens + outputTokens)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        _ctx.Add(new ConversationUsage
        {
            ConversationId = conversation.Id,
            UserId = conversation.UserId,
            ModelId = request.ModelId,
            ProviderId = model.ProviderId,
            DeploymentName = model.DeploymentName,
            Kind = ConversationUsageKinds.Naming,
            Status = ConversationUsageStatuses.Completed,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedInputTokens,
            ReasoningTokens = reasoningTokens,
            // Priced like any other call: naming is a real, billed completion.
            InputPricePerMillionTokens = model.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = model.OutputPricePerMillionTokens
        });

        await _ctx.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Patch only what changed: a full replace with the SQL entity would destroy the transcript
        // mid-stream.
        await _transcriptStore.PatchItemAsync(
            conversation.Id.ToString(), TranscriptPartition(conversation.UserId, conversation.Id),
        [
            PatchOperation.Set("/name", name),
            PatchOperation.Set("/dateModified", date),
        ], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PaginatedResponseDto<ConversationDto>> SearchConversationsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, Guid? projectId = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default)
    {
        // Resolved before any database work, so a misspelled sort field costs a round trip to
        // nothing rather than a page the caller then has to distrust.
        SortSelection<ConversationSortKeys>? order = SortParameters.IsDefaultOrder(sort, dir)
            ? null
            : ConversationSortKeyNames.Resolve(sort, dir);

        // Clamped rather than validated: the paging arguments come straight off the query
        // string, and take = 0 would divide by zero when computing CurrentPage below.
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);

        var userId = _tokenService.GetOid();

        var query = _ctx.Conversations
            .AsNoTracking()
            .Where(x => x.UserId == userId);

        // A missing, deactivated or foreign project answers 404 rather than an empty page, so the
        // route cannot probe project ids; a deactivated project's released conversations would look empty.
        if (projectId.HasValue)
        {
            await EnsureProjectExistsAsync(projectId.Value, userId, cancellationToken);
            query = query.Where(x => x.ProjectId == projectId.Value);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => !string.IsNullOrWhiteSpace(x.Name) && EF.Functions.Like(x.Name, $"%{name}%"));
        }

        if (isFavorite.HasValue)
        {
            query = query.Where(x => x.IsFavorite == isFavorite.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await OrderConversations(query, order)
            .Skip(skip)
            .Take(take)
            .Select(ConversationMapper.MapToChatDtoExpression)
            .ToListAsync(cancellationToken);

        return new PaginatedResponseDto<ConversationDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageSize = take,
            CurrentPage = (skip / take) + 1
        };
    }

    public async Task<ConversationDto> UpdateConversationAsync(UpdateConversationActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();

        var anyConversation = await _ctx.Conversations
            .Where(x => x.Id == request.Id && x.UserId == userId)
            .AnyAsync(cancellationToken);
        if (!anyConversation)
        {
            LogUpdatedConversationNotFound(_logger);
            throw new NotFoundException($"Conversation not found or already deactivated.");
        }

        if (request.ProjectId.HasValue)
        {
            await EnsureProjectExistsAsync(request.ProjectId.Value, userId, cancellationToken);
        }

        var date = _timeProvider.GetUtcNow();
        // Full-representation PUT: an absent ProjectId removes the conversation from its project.
        var rows = await _ctx.Conversations
            .Where(x => x.Id == request.Id && x.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, request.Name)
                .SetProperty(x => x.ProjectId, request.ProjectId)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _transcriptStore.PatchItemAsync(
            request.Id.ToString(), TranscriptPartition(userId, request.Id),
        [
            PatchOperation.Set("/name", request.Name),
            PatchOperation.Set("/dateModified", date),
        ], cancellationToken);

        // Not GetConversationAsync: its MCP-selection round trip fills a field ConversationDto lacks.
        return await _ctx.Conversations
            .AsNoTracking()
            .Where(x => x.Id == request.Id)
            .Select(ConversationMapper.MapToChatDtoExpression)
            .FirstAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetConversationFavoriteAsync(Guid id, SetConversationFavoriteActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();
        var date = _timeProvider.GetUtcNow();

        // No Cosmos patch, which keeps a star click off a live turn's document. DateModified is left
        // alone so SQL stays in step with the Cosmos timestamp the transcript endpoint returns.
        var rows = await _ctx.Conversations
            .Where(x => x.Id == id && x.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsFavorite, request.IsFavorite),
                cancellationToken);

        if (rows == 0)
        {
            LogConversationNotFoundOrDeactivated(_logger, id);
            throw new NotFoundException($"Conversation with id {id} not found");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not an iterator, so the argument check runs on the call. The lock is taken inside the iterator (a
    /// caller that never enumerates cannot strand it) but before the first fragment, so contention is a 409.
    /// </remarks>
    public IAsyncEnumerable<AssistantUiEvent> StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();

        return StreamConversationCoreAsync(id, request, userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ChatConversationDto> GetConversationMessagesAsync(
        Guid id, int? take, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var userId = _tokenService.GetOid();

        // The partition key includes the caller's id, so another user's conversation reads null: a
        // 404, never a 403.
        var header = await ReadHeaderAsync(userId, id, cancellationToken);

        if (header is null)
        {
            // A pre-cutover conversation has a SQL row but no header. An empty transcript is the
            // truth about it, and an error would strand a row the user sees in their sidebar.
            return await ReadEmptyTranscriptAsync(userId, id, cancellationToken);
        }

        if (header.DateDeactivated.HasValue)
        {
            LogTranscriptConversationNotFound(_logger, id);
            throw new NotFoundException($"Conversation {id} not found.");
        }

        // Clamped, not validated, like the other paginated routes. Absent means the whole transcript:
        // paging is opt-in.
        var clamped = take.HasValue ? Math.Clamp(take.Value, 1, 100) : (int?)null;

        // One extra to answer "is there another page"; the header's total cannot, because walking
        // backwards it would always claim another page follows.
        var probe = clamped.HasValue ? clamped.Value + 1 : (int?)null;
        var fetched = await ReadMessagesAsync(userId, id, probe, before, cancellationToken);

        var hasMore = clamped.HasValue && fetched.Count > clamped.Value;
        var documents = hasMore ? [.. fetched.TakeLast(clamped!.Value)] : fetched;

        var messages = documents
                        .Where(x => x.Role != ChatRoles.System)
                        .Select(x => new ConversationMessageDto()
                        {
                            Id = Guid.TryParse(x.Id, out var messageId) ? messageId : Guid.Empty,
                            DateCreated = x.DateCreated,
                            Text = x.Content ?? string.Empty,
                            Role = x.Role,
                            HtmlContent = x.HtmlContent,
                            Tokens = x.Tokens,
                            TokenAccuracy = x.TokenAccuracy,
                            // Off the document, not joined from the usage row, which is keyed the
                            // other way and would cost a query per page.
                            Usage = x.Usage is null
                                ? null
                                : new ConversationMessageUsageDto
                                {
                                    InputTokens = x.Usage.InputTokens,
                                    OutputTokens = x.Usage.OutputTokens
                                },
                            // Off the document too: this projection is all a reload gets, so the
                            // relational row cannot stand in for it.
                            Feedback = x.Feedback is null
                                ? null
                                : new ConversationMessageFeedbackDto
                                {
                                    Rating = x.Feedback.Rating,
                                    DateModified = x.Feedback.DateModified
                                },
                            // The whole of how a generated file survives a reload: the chip is
                            // drawn from this, and the link is minted when it is clicked.
                            Attachments = [.. x.Attachments.Select(a => new MessageAttachmentDto
                            {
                                Id = a.Id,
                                Name = a.Name,
                                Extension = a.Extension,
                                MimeType = a.MimeType,
                                Size = a.Size
                            })]
                        })
                        .ToList();

        return new()
        {
            Id = id,
            Name = header.Name,
            DateCreated = header.DateCreated,
            DateModified = header.DateModified,
            Messages = messages,
            // The header's count (exact thanks to the batched writes) includes the system message the
            // payload omits, so the oldest page of a paged read comes back one short of `take`.
            TotalCount = header.MessageCount,
            HasMore = hasMore
        };
    }

    /// <inheritdoc />
    public async Task SetMessageFeedbackAsync(
        Guid conversationId, Guid messageId, SetMessageFeedbackActionDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();

        // Establishes the 404 for an unknown, deactivated or foreign conversation, and satisfies
        // the projection row's foreign key before anything tries to write it.
        await EnsureConversationExistsAsync(conversationId, userId, cancellationToken);

        // No ownership check needed: the partition key combines the caller and conversation ids, so
        // a message from elsewhere reads null.
        var message = await _transcriptStore.ReadItemAsync<TranscriptMessageDocument>(
            messageId.ToString(), TranscriptPartition(userId, conversationId), cancellationToken);

        // The id comes off the route, and the header shares this partition under the conversation id,
        // so without the type check a conversation id would point-read the header as a message.
        if (message is null || message.Type is not TranscriptDocumentTypes.Message)
        {
            LogMessageNotFoundInConversation(_logger, messageId, conversationId);

            throw new NotFoundException($"Message with id {messageId} not found");
        }

        if (message.Role is not ChatRoles.Assistant)
        {
            // Keyed on the route parameter, as the sort and export parameters are: the body is
            // well-formed, but the message it names cannot be rated.
            throw new ValidationException(
            [
                new ValidationFailure(
                    "messageId", "Feedback can only be recorded against an assistant message.")
            ]);
        }

        var ratedAt = _timeProvider.GetUtcNow();

        var written = await PatchTranscriptFeedbackAsync(
            message, userId, conversationId, messageId, request.Rating, ratedAt, cancellationToken);

        if (!written)
        {
            // Withdrawing an unrecorded rating; projecting it would inflate "messages considered",
            // the denominator reporting reads.
            return;
        }

        await ProjectFeedbackAsync(
            userId, conversationId, messageId, request.Rating, cancellationToken);
    }

    /// <summary>
    /// Everything a finished turn needs to record, whether it ran to completion or was abandoned.
    /// </summary>
    /// <param name="ConversationId">The conversation.</param>
    /// <param name="UserId">The user the turn is charged to.</param>
    /// <param name="Model">The catalog model that served the turn.</param>
    /// <param name="McpServers">Empty when none were selected.</param>
    /// <param name="Status">How the turn ended.</param>
    /// <param name="Prompt">The user's prompt.</param>
    /// <param name="Answer">Possibly truncated when the turn was abandoned.</param>
    /// <param name="PromptReceivedAt">Stamps the user message.</param>
    /// <param name="HeaderExists">False for a pre-cutover conversation, whose header the turn creates.</param>
    /// <param name="ConversationName">Only for creating the header.</param>
    /// <param name="ConversationCreatedAt">Only for creating the header, so it keeps the real date.</param>
    /// <param name="EstimatedPromptTokens">Includes overhead; compared with the provider's figure.</param>
    /// <param name="Report">The middleware's accounting, or <see langword="null"/> when there is none.</param>
    /// <param name="Attachments">Files the File Agent stored; empty for a stopped or faulted turn.</param>
    /// <param name="FileAgent">The agent's pinned model, or <see langword="null"/> when not attached.</param>
    private sealed record TurnOutcome(
        Guid ConversationId,
        Guid UserId,
        ModelDto Model,
        IReadOnlyList<McpServerReference> McpServers,
        ConversationUsageStatuses Status,
        string Prompt,
        string Answer,
        DateTimeOffset PromptReceivedAt,
        bool HeaderExists,
        string ConversationName,
        DateTimeOffset ConversationCreatedAt,
        long EstimatedPromptTokens,
        ChatUsageReport? Report,
        IReadOnlyList<MessageAttachmentDto> Attachments,
        FileAgentModel? FileAgent);

    #region Private methods

    /// <remarks>
    /// Names compare by database collation (case-insensitive on SQL Server, ordinal on SQLite), so a
    /// client must not re-sort a page with <c>localeCompare</c>.
    /// </remarks>
    private static IOrderedQueryable<Conversation> OrderConversations(
        IQueryable<Conversation> query,
        SortSelection<ConversationSortKeys>? order)
    {
        if (order is not { } requested)
        {
            // Deliberately no tie-break: adding Id would reorder same-tick conversations, and the
            // default order is promised unchanged.
            return query.OrderByDescending(x => x.DateCreated);
        }

        var ordered = requested.Key switch
        {
            ConversationSortKeys.Name => requested.IsAscending
                ? query.OrderBy(x => x.Name)
                : query.OrderByDescending(x => x.Name),
            _ => requested.IsAscending
                ? query.OrderBy(x => x.DateCreated)
                : query.OrderByDescending(x => x.DateCreated)
        };

        // A tie-break for stable paging, following the requested direction: untitled conversations
        // share a name, so a fixed tie-break would make A-Z and Z-A look identical.
        return requested.IsAscending
            ? ordered.ThenBy(x => x.Id)
            : ordered.ThenByDescending(x => x.Id);
    }

    /// <summary>
    /// Runs a single chat turn under the conversation lock: loads history, streams the model
    /// response and its tool activity to the caller, then persists the turn.
    /// </summary>
    private async IAsyncEnumerable<AssistantUiEvent> StreamConversationCoreAsync(Guid id, CreateConversationStreamActionDto request, Guid userId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // using var keeps acquire and release inseparable: a statement slipped between them could
        // throw and strand the lock until a restart.
        using var lockReleaser = await _conversationLockService.TryAcquireLockAsync(id, cancellationToken)
            ?? throw LogAndCreateBusy(id);

        var conversation = await _ctx.Conversations
                        .AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == id &&
                            x.UserId == userId, cancellationToken);
        if (conversation is null)
        {
            LogStreamConversationNotFound(_logger, id);
            throw new NotFoundException($"Conversation with id {id} not found.");
        }

        // Stamps the user message when it was asked, not when the answer finished, which keeps
        // dateCreated a total order.
        var promptReceivedAt = _timeProvider.GetUtcNow();

        // A one-RU point read: is this the first turn, and does a header exist at all? Pre-cutover
        // conversations have none, and patching an absent header would fail the whole batch.
        var header = await ReadHeaderAsync(userId, id, cancellationToken);

        // Only the seeded system prompt so far: name the conversation from its opening prompt.
        if (header is { MessageCount: 1 })
        {
            await UpdateConversationNameAsync(id, request, cancellationToken);
        }

        var transcript = await ReadMessagesAsync(userId, id, take: null, before: null, cancellationToken);

        var conversations = new List<ChatMessage>(transcript.Select(x => new ChatMessage(ChatRoleMapper.MapToChatRole(x.Role), x.Content)))
        {
            new(ChatRole.User, request.Prompt)
        };

        // Read per turn and carried on the chat options, not the transcript, so editing a project's
        // instructions or moving the conversation between projects takes effect.
        var projectInstructions = await GetProjectInstructionsAsync(conversation.ProjectId, cancellationToken);

        var model = await _modelService.GetModelAsync(request.ModelId, cancellationToken);
        var chatClient = _chatClientResolver.Resolve(model.ProviderId);
        var (chatOptions, toolLeases, fileAgentLease) = await CreateChatOptionsAsync(id, model, request.McpServers, projectInstructions, cancellationToken).ConfigureAwait(false);

        StringBuilder sb = new();
        var completed = false;

        // Held back: the frame carries the assistant message id, which exists only once
        // PersistTurnAsync runs in the finally below. Re-emitted after the lease block.
        AssistantUiEvent? finishedEvent = null;
        Guid? assistantMessageId = null;

        // Copied while the lease is open; the frame carrying it is yielded after the scope closes.
        IReadOnlyList<MessageAttachmentDto> generatedAttachments = [];

        // A completed turn is owed its Finished frame whatever the write does, so a write failure is
        // captured in the finally and rethrown after the frame.
        ExceptionDispatchInfo? persistFailure = null;

        // The leases (null without MCPs) must outlive the stream: the tools invoke through them.
        await using (fileAgentLease)
        await using (toolLeases)
        {
            // Inside the lease scope because this throws when the prompt cannot fit, and a throw
            // before the scope would strand the leases and the MCP clients they hold.
            var estimator = _tokenEstimatorResolver.Resolve(model.ProviderId);
            var estimatedPromptTokens = ApplyContextBudget(
                conversations, transcript, request.Prompt, model, chatOptions, estimator);

            // Counts read off the stream would be lost for every cancelled turn, which never
            // reaches its final update.
            var usageScope = ChatUsageScope.Create();

            // Enumerated by hand so the scope can be re-entered around each move and the final
            // disposal: an async iterator loses ambient state after a yield.
            var events = chatClient
                .GetStreamingResponseAsync(conversations, chatOptions, cancellationToken)
                .ToUiEventsAsync(cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            // A cancelled or faulted turn is still billed, and the finally is the only path that
            // runs when a disconnect disposes the enumerator. A catch cannot enclose a yield, hence the flag.
            try
            {
                // After every failure point that can still answer with problem JSON. The first yield
                // commits to text/event-stream, so a first-token fault becomes a truncated stream (a
                // deliberate trade for early feedback); in the try so a disconnect still records the turn.
                yield return new AssistantUiEvent
                {
                    Kind = AssistantUiEventKind.Status,
                    Message = StartingStatusMessage,
                    Timestamp = _timeProvider.GetUtcNow()
                };

                // A deliberate, product-requested deviation: ReasoningDelta is documented as
                // model-sourced. Timestamp stays default, as the contract prescribes for deltas.
                yield return new AssistantUiEvent
                {
                    Kind = AssistantUiEventKind.ReasoningDelta,
                    Text = PreparingReasoningText
                };

                while (true)
                {
                    bool moved;
                    using (usageScope.Enter())
                    {
                        moved = await events.MoveNextAsync();
                    }

                    if (!moved)
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var uiEvent = events.Current;

                    // Only the answer is transcribed; reasoning is shown live but never replayed
                    // to the model.
                    if (uiEvent.Kind is AssistantUiEventKind.TextDelta && !string.IsNullOrEmpty(uiEvent.Text))
                    {
                        sb.Append(uiEvent.Text);
                    }

                    if (uiEvent.Kind is AssistantUiEventKind.Finished)
                    {
                        finishedEvent = uiEvent;
                        continue;
                    }

                    yield return uiEvent;
                }

                completed = true;
            }
            finally
            {
                // Before the report is read: disposal finalizes an abandoned request. Guarded so a
                // teardown fault cannot replace the in-flight exception and skip the accounting.
                try
                {
                    using (usageScope.Enter())
                    {
                        await events.DisposeAsync();
                    }
                }
                catch (Exception ex)
                {
                    LogDisposingResponseStreamFailed(_logger, ex, id);
                }

                // An abandoned turn's files would have no message introducing them. Withdrawn before
                // the attachments are read, so the audit row records what the user actually got.
                if (!completed && fileAgentLease is not null)
                {
                    await fileAgentLease.DiscardGeneratedAsync();
                }

                generatedAttachments = [.. fileAgentLease?.GeneratedDocuments ?? []];

                var turn = new TurnOutcome(
                    ConversationId: id,
                    UserId: userId,
                    Model: model,
                    McpServers: toolLeases?.Servers ?? [],
                    Status: completed
                        ? ConversationUsageStatuses.Completed
                        : cancellationToken.IsCancellationRequested
                            ? ConversationUsageStatuses.Cancelled
                            : ConversationUsageStatuses.Failed,
                    Prompt: request.Prompt,
                    Answer: sb.ToString(),
                    PromptReceivedAt: promptReceivedAt,
                    HeaderExists: header is not null,
                    ConversationName: conversation.Name,
                    ConversationCreatedAt: conversation.DateCreated,
                    EstimatedPromptTokens: estimatedPromptTokens,
                    Report: usageScope.Report,
                    Attachments: generatedAttachments,
                    FileAgent: fileAgentLease?.Model);

                // The token that ended the stream is the one that would abort the write
                // recording it, so finalization runs uncancellable.
                try
                {
                    assistantMessageId = await PersistTurnAsync(turn, CancellationToken.None);
                }
                catch (Exception ex) when (!completed)
                {
                    // Throwing would replace the in-flight exception, turning a cancelled turn's 499
                    // into a database error; losing the usage row is the lesser harm.
                    LogAbandonedTurnUsageFailed(_logger, ex, id);
                }
                catch (Exception ex)
                {
                    // Rethrown after the Finished frame, so a completed turn still reads as complete
                    // (e.g. a Render failure after the SQL commit).
                    persistFailure = ExceptionDispatchInfo.Capture(ex);
                }
            }
        }

        // Reached only on a completed turn: abandoned turns get no Finished event, and a disconnect
        // disposes the iterator through the finally, so no abandoned turn claims a message id.
        if (finishedEvent is not null)
        {
            // No message id when the transcript write failed: the id promises a readable message.
            // Assigned, not merged, because ToUiEventsAsync always leaves Metadata null.
            Dictionary<string, string> metadata = [];

            if (assistantMessageId is { } messageId)
            {
                metadata[IConversationService.AssistantMessageIdMetadataKey] = messageId.ToString();
            }

            // Only on the turn that made them, once stored, so the chip appears without a reload.
            if (generatedAttachments.Count > 0)
            {
                metadata[IConversationService.GeneratedAttachmentsMetadataKey] =
                    JsonSerializer.Serialize(generatedAttachments);
            }

            yield return metadata.Count > 0 ? finishedEvent with { Metadata = metadata } : finishedEvent;
        }

        // After the frame, never instead of it; GlobalExceptionHandler leaves a started response alone.
        persistFailure?.Throw();
    }

    /// <summary>
    /// Records a finished turn: the conversation's running counters and last model, the usage
    /// audit row with the tool calls underneath it, and — only for a turn that completed — the
    /// transcript append.
    /// </summary>
    /// <returns>
    /// The appended assistant message's id, or <see langword="null"/> for an abandoned turn or a
    /// failed transcript write.
    /// </returns>
    /// <remarks>
    /// An abandoned turn is billed but not transcribed: a truncated answer would corrupt replayed history.
    /// <see cref="ConversationUsage.AssistantMessageId"/> is set before the append, so it is best-effort.
    /// </remarks>
    private async Task<Guid?> PersistTurnAsync(TurnOutcome turn, CancellationToken cancellationToken)
    {
        var date = _timeProvider.GetUtcNow();
        var completed = turn.Status == ConversationUsageStatuses.Completed;
        var assistantMessageId = Guid.NewGuid();
        var conversationId = turn.ConversationId;
        var modelId = turn.Model.Id;

        if (turn.Report is null)
        {
            // Tracking is on every client, so no report means an early fault. Recorded at zero
            // rather than dropped: a zero-token row explains the gap in the audit trail.
            LogNoUsageReportForTurn(_logger, turn.Status, conversationId);
        }

        var turnUsage = UsageReportTranslator.Translate(turn.Report, turn.McpServers, turn.FileAgent);

        if (turn.FileAgent is null
            && turnUsage.ToolCalls.Exists(call => call.Kind == ConversationToolKinds.Agent))
        {
            // The startup validator should prevent this. Logged, not guessed: the column is a
            // foreign key and the answer has already streamed.
            LogAgentToolCallWithoutModel(_logger, conversationId);
        }

        // Tool-free turns only: tool round trips re-send prompts the estimator never saw. Not a
        // token-zero test, since an MCP server can run and report no usage.
        if (turn.Report is not null && turnUsage.ToolCalls.Count == 0)
        {
            ChatMetrics.RecordEstimatorError(
                turn.Model.ProviderId,
                turn.Model.DeploymentName,
                turn.EstimatedPromptTokens,
                turnUsage.InputTokens);
        }

        // Everything the turn consumed, tools included, from the audit-row numbers so the counters
        // always match the rows.
        var inputTokens = turnUsage.TotalInputTokens;
        var outputTokens = turnUsage.TotalOutputTokens;

        // Estimated before the transaction so the roll-up rides the running update. Null, not zero,
        // for a turn billed but never transcribed.
        var estimator = _tokenEstimatorResolver.Resolve(turn.Model.ProviderId);
        var deploymentName = turn.Model.DeploymentName;

        long? contextTokens = null;
        long promptTokens = 0;
        long answerTokens = 0;
        TranscriptMessageDocument? reseededSystemMessage = null;

        if (completed)
        {
            promptTokens = EstimateTokens(estimator, turn.Prompt, deploymentName);
            answerTokens = EstimateTokens(estimator, turn.Answer, deploymentName);

            // The turn's own two messages; a reseeded system message belongs to the conversation.
            contextTokens = promptTokens + answerTokens;

            if (!turn.HeaderExists)
            {
                reseededSystemMessage = BuildSeededSystemMessage(
                    turn.UserId, conversationId, turn.PromptReceivedAt.AddTicks(-1), estimator, deploymentName);
            }
        }

        // The conversation roll-up counts every message written, so it stays equal to the
        // header's own counter; the per-turn figure above counts only the turn's two.
        var contextDelta = (contextTokens ?? 0) + (reseededSystemMessage?.Tokens ?? 0);

        // One transaction, so a conversation whose counters moved always carries the usage row
        // that accounts for the movement.
        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        await _ctx.Conversations
            .Where(s => s.Id == conversationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.InputTokens, x => x.InputTokens + inputTokens)
                .SetProperty(x => x.OutputTokens, x => x.OutputTokens + outputTokens)
                .SetProperty(x => x.ContextTokens, x => x.ContextTokens + contextDelta)
                .SetProperty(x => x.ModelId, modelId)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        // Id left to EF's sequential generator: random GUIDs would fragment the clustered key of
        // the schema's fastest-growing table.
        var usage = new ConversationUsage
        {
            ConversationId = turn.ConversationId,
            UserId = turn.UserId,
            ModelId = turn.Model.Id,
            ProviderId = turn.Model.ProviderId,
            DeploymentName = turn.Model.DeploymentName,
            Kind = ConversationUsageKinds.Chat,
            Status = turn.Status,
            InputTokens = turnUsage.InputTokens,
            OutputTokens = turnUsage.OutputTokens,
            ToolInputTokens = turnUsage.ToolInputTokens,
            ToolOutputTokens = turnUsage.ToolOutputTokens,
            CachedInputTokens = turnUsage.CachedInputTokens,
            ReasoningTokens = turnUsage.ReasoningTokens,
            // Snapshotted for the same reason DeploymentName is: an administrator editing the
            // catalog must not rewrite what past calls cost.
            InputPricePerMillionTokens = turn.Model.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = turn.Model.OutputPricePerMillionTokens,
            ContextTokens = contextTokens,
            AssistantMessageId = completed ? assistantMessageId : null,
            McpServers =
            [
                .. turn.McpServers.Select(server => new ConversationUsageMcpServer
                {
                    McpServerId = server.Id,
                    McpServerName = server.Name
                })
            ],
            // One graph, so EF assigns sequential keys and fixes each child's ParentId.
            ToolCalls = turnUsage.ToolCalls,
            // Empty on a non-streamed call, so a tool row's Iteration only means something alongside these.
            Turns = turnUsage.Turns
        };

        _ctx.Add(usage);
        await _ctx.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (!completed)
        {
            return null;
        }

        // At least one tick after the prompt: dateCreated is the only order, so identical stamps
        // could put the answer before the question, in display and in every replay.
        var answeredAt = date > turn.PromptReceivedAt ? date : turn.PromptReceivedAt.AddTicks(1);

        // The deployment name, not the display label: the append-only transcript must stay
        // meaningful after a catalog rename.
        var userMessage = TranscriptMessageDocument.Create(
            turn.UserId, conversationId, Guid.NewGuid(), ChatRoles.User, turn.Prompt, turn.PromptReceivedAt) with
        {
            Tokens = promptTokens,
            HtmlContent = _markdownRenderer.Render(turn.Prompt),
            Model = deploymentName
        };

        var assistantMessage = TranscriptMessageDocument.Create(
            turn.UserId, conversationId, assistantMessageId, ChatRoles.Assistant, turn.Answer, answeredAt) with
        {
            Tokens = answerTokens,
            HtmlContent = _markdownRenderer.Render(turn.Answer),
            Model = deploymentName,
            Usage = new TranscriptMessageUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            },
            Attachments = [.. turn.Attachments.Select(a => new TranscriptMessageAttachment
            {
                Id = a.Id,
                Name = a.Name,
                Extension = a.Extension,
                MimeType = a.MimeType,
                Size = a.Size
            })]
        };

        // One batch, so the header's counters and the documents they count land together, which
        // keeps messageCount exact.
        var result = await _transcriptStore.ExecuteBatchAsync(
            TranscriptPartition(turn.UserId, conversationId),
            batch =>
            {
                batch.Create(userMessage.Id, userMessage)
                     .Create(assistantMessage.Id, assistantMessage);

                if (turn.HeaderExists)
                {
                    batch.Patch(conversationId.ToString(),
                    [
                        PatchOperation.Increment("/messageCount", 2),
                        PatchOperation.Increment("/contextTokens", contextDelta),
                        PatchOperation.Set("/dateModified", answeredAt),
                    ]);
                }
                else
                {
                    // Pre-cutover: no header to patch, and the seeded system message must be restored
                    // or every later turn silently runs without the standing instructions.
                    batch.Create(reseededSystemMessage!.Id, reseededSystemMessage);

                    // The real creation date, not now, or the conversation would report two
                    // creation dates either side of this turn.
                    batch.Create(
                        conversationId.ToString(),
                        TranscriptHeaderDocument.Create(
                            turn.UserId, conversationId, turn.ConversationName, turn.ConversationCreatedAt) with
                        {
                            MessageCount = 3,
                            ContextTokens = contextDelta,
                            DateModified = answeredAt
                        });
                }
            },
            cancellationToken);

        if (!result.IsSuccess)
        {
            // Billed in SQL and already streamed, so only the logs can catch this. Both ids are
            // logged because the usage row now names a message no transcript contains.
            LogTranscriptWriteFailed(_logger, conversationId, result.Describe(), usage.Id, assistantMessageId);

            return null;
        }

        return assistantMessageId;
    }

    /// <summary>
    /// Throws when the conversation does not exist, is deactivated, or belongs to another user.
    /// </summary>
    private async Task EnsureConversationExistsAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        var exists = await _ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == id && x.UserId == userId, cancellationToken);

        if (!exists)
        {
            // Deliberately indistinguishable from "does not exist" so this cannot be used to
            // probe for conversation ids belonging to other users.
            LogConversationNotAccessible(_logger, id, userId);
            throw new NotFoundException($"Conversation with id {id} not found");
        }
    }

    /// <summary>
    /// Throws when the project does not exist, is deactivated, or belongs to another user.
    /// </summary>
    private async Task EnsureProjectExistsAsync(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var exists = await _ctx.Projects
            .AsNoTracking()
            .AnyAsync(x => x.Id == projectId && x.UserId == userId, cancellationToken);

        if (!exists)
        {
            // Deliberately indistinguishable from "does not exist" so this cannot be used to
            // probe for project ids belonging to other users.
            LogProjectNotAccessible(_logger, projectId, userId);
            throw new NotFoundException($"Project with id '{projectId}' was not found.");
        }
    }

    /// <summary>
    /// Reads the standing instructions of the conversation's project, if it has one.
    /// </summary>
    /// <remarks>
    /// Ownership is not re-checked: the caller matched the conversation, and a project only holds its
    /// owner's conversations.
    /// </remarks>
    private async Task<string?> GetProjectInstructionsAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue)
        {
            return null;
        }

        return await _ctx.Projects
            .AsNoTracking()
            .Where(x => x.Id == projectId.Value)
            .Select(x => x.Instructions)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private ConversationBusyException LogAndCreateBusy(Guid id)
    {
        LogConversationBusy(_logger, id);

        return new ConversationBusyException($"Conversation {id} is currently being processed. Please wait for the current request to complete.");
    }

    /// <summary>
    /// Trims a turn's replayed history in place to what the selected model's context window can
    /// hold, and reports the estimated prompt that results, including the per-turn overhead.
    /// </summary>
    /// <exception cref="ValidationException">
    /// The system message and prompt alone exceed the window; failing now beats a provider rejection later.
    /// </exception>
    private long ApplyContextBudget(
        List<ChatMessage> replay,
        IReadOnlyList<TranscriptMessageDocument> transcript,
        string prompt,
        ModelDto model,
        ChatOptions chatOptions,
        ITokenEstimator estimator)
    {
        List<BudgetMessage> budgetMessages =
        [
            // Messages stored before per-message accounting carry zero; estimated on the fly so
            // they are not free to keep.
            .. transcript.Select(x => new BudgetMessage(
                x.Role,
                x.Tokens > 0 ? x.Tokens : EstimateTokens(estimator, x.Content, model.DeploymentName))),
            new(ChatRoles.User, EstimateTokens(estimator, prompt, model.DeploymentName))
        ];

        // Project and document instructions ride the options rather than the message list, so
        // they are billed and belong to no message. Counted into the overhead for that reason.
        var overhead = _promptOverheadCalculator.Estimate(
            budgetMessages.Count,
            chatOptions.Tools?.Count ?? 0,
            EstimateTokens(estimator, chatOptions.Instructions, model.DeploymentName));

        var budget = ContextBudgetCalculator.Apply(
            budgetMessages, model.ContextWindowSize, model.MaxOutputTokens, overhead);

        if (!budget.Fits)
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    nameof(CreateConversationStreamActionDto.Prompt),
                    $"This prompt does not fit the context window of '{model.Name}' ({model.ContextWindowSize:0} tokens, reserving {model.MaxOutputTokens:0} for the answer). Shorten the prompt or choose a model with a larger context window.")
            ]);
        }

        if (budget.IsUnbounded)
        {
            LogUnboundedModelOnce(model);
        }

        if (budget.DroppedMessages > 0)
        {
            ChatMetrics.RecordBudgetTrim(model.DeploymentName, budget.DroppedMessages, budget.DroppedTokens);

            LogHistoryTrimmed(_logger, budget.DroppedMessages, budget.DroppedTokens, model.DeploymentName);

            var kept = budget.KeptIndexes.Select(index => replay[index]).ToList();
            replay.Clear();
            replay.AddRange(kept);
        }

        return budget.KeptIndexes.Sum(index => budgetMessages[index].Tokens) + overhead;
    }

    private void LogUnboundedModelOnce(ModelDto model)
    {
        if (_unboundedModelsReported.TryAdd(model.Id, true))
        {
            LogContextWindowUnbounded(_logger, model.Id, model.Name);
        }
    }

    /// <summary>
    /// Answers a transcript read for a conversation that has a relational row but no transcript.
    /// </summary>
    /// <remarks>
    /// The only SQL fallback: pre-cutover conversations lost their transcripts, but a sidebar row must open.
    /// </remarks>
    private async Task<ChatConversationDto> ReadEmptyTranscriptAsync(
        Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var conversation = await _ctx.Conversations
            .AsNoTracking()
            .Where(x => x.Id == id && x.UserId == userId)
            .Select(x => new { x.Name, x.DateCreated, x.DateModified })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversation is null)
        {
            LogTranscriptConversationNotFound(_logger, id);
            throw new NotFoundException($"Conversation {id} not found.");
        }

        LogTranscriptMissing(_logger, id);

        return new ChatConversationDto
        {
            Id = id,
            Name = conversation.Name,
            DateCreated = conversation.DateCreated,
            DateModified = conversation.DateModified,
            Messages = []
        };
    }

    /// <summary>
    /// Builds the seeded system message a conversation's transcript opens with.
    /// </summary>
    /// <remarks>
    /// <paramref name="createdAt"/> must precede every other message. Estimated and rendered like user
    /// text, so the header's counters share one basis.
    /// </remarks>
    private TranscriptMessageDocument BuildSeededSystemMessage(
        Guid userId,
        Guid conversationId,
        DateTimeOffset createdAt,
        ITokenEstimator estimator,
        string? deploymentName)
    {
        var prompt = ConversationPrompts.BuildDefaultSystemPrompt();

        return TranscriptMessageDocument.Create(
            userId, conversationId, Guid.NewGuid(), ChatRoles.System, prompt, createdAt) with
        {
            Tokens = EstimateTokens(estimator, prompt, deploymentName),
            HtmlContent = _markdownRenderer.Render(prompt)
        };
    }

    /// <summary>
    /// Counts a message's context cost, degrading to zero rather than failing the turn.
    /// </summary>
    /// <remarks>
    /// A tokenizer fault must not lose an already-streamed turn, and zero is recoverable: the count is a
    /// pure function of the stored text.
    /// </remarks>
    private long EstimateTokens(ITokenEstimator estimator, string? text, string? deploymentName)
    {
        try
        {
            return estimator.CountTokens(text, deploymentName);
        }
        catch (Exception ex)
        {
            LogTokenEstimationFailed(_logger, ex);

            return 0;
        }
    }

    /// <remarks>
    /// Always composed from the caller's id, so a foreign conversation id addresses an empty partition.
    /// </remarks>
    private static PartitionKey TranscriptPartition(Guid userId, Guid conversationId)
    {
        return new PartitionKey(PartitionKeys.For(userId, conversationId));
    }

    /// <summary>
    /// Reads a conversation's header document, or <see langword="null"/> when it has no transcript.
    /// </summary>
    private Task<TranscriptHeaderDocument?> ReadHeaderAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        return _transcriptStore.ReadItemAsync<TranscriptHeaderDocument>(
            conversationId.ToString(), TranscriptPartition(userId, conversationId), cancellationToken);
    }

    /// <summary>
    /// Reads a conversation's messages oldest first: the newest <paramref name="take"/> (or all) written
    /// strictly before <paramref name="before"/> (or now).
    /// </summary>
    /// <remarks>
    /// The <c>type</c> predicate keeps later document kinds out. Ordered by <c>dateCreated</c> alone because
    /// a two-key ORDER BY needs a composite index and turns cannot tie; id still breaks ties in memory.
    /// </remarks>
    private async Task<List<TranscriptMessageDocument>> ReadMessagesAsync(
        Guid userId,
        Guid conversationId,
        int? take,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        var partition = TranscriptPartition(userId, conversationId);
        var descending = take.HasValue || before.HasValue;

        var text = descending
            ? "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId AND (IS_NULL(@before) OR c.dateCreated < @before) ORDER BY c.dateCreated DESC"
            : "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId ORDER BY c.dateCreated";

        var query = new QueryDefinition(text)
            .WithParameter("@type", TranscriptDocumentTypes.Message)
            .WithParameter("@conversationId", conversationId);

        if (descending)
        {
            query = query.WithParameter("@before", before);
        }

        List<TranscriptMessageDocument> messages = [];
        string? continuationToken = null;

        do
        {
            var page = await _transcriptStore.QueryAsync<TranscriptMessageDocument>(
                query, partition, take, continuationToken, cancellationToken);

            messages.AddRange(page.Items);
            continuationToken = page.ContinuationToken;

            // Drain until the count is in hand: Cosmos can return short or even empty pages with more
            // behind, and silently truncated history would be replayed to the model.
            if (take.HasValue && messages.Count >= take.Value)
            {
                break;
            }
        }
        while (continuationToken is not null);

        var ordered = messages
            .OrderBy(x => x.DateCreated)
            .ThenBy(x => x.Id, StringComparer.Ordinal);

        // A bounded read asks for the newest N, so the surplus to discard is at the old end.
        return take.HasValue && messages.Count > take.Value
            ? [.. ordered.TakeLast(take.Value)]
            : [.. ordered];
    }

    /// <summary>
    /// Writes the rating onto the transcript message document, which is what the transcript
    /// response reads back.
    /// </summary>
    /// <returns><see langword="false"/> when withdrawing a rating that was never recorded.</returns>
    /// <exception cref="NotFoundException">The document was deleted between the read and the patch.</exception>
    private async Task<bool> PatchTranscriptFeedbackAsync(
        TranscriptMessageDocument message, Guid userId, Guid conversationId, Guid messageId,
        MessageFeedbackRatings? rating, DateTimeOffset ratedAt, CancellationToken cancellationToken)
    {
        if (rating is null && message.Feedback is null)
        {
            // Safe to decide from the read here: skipping a write leaves the document as found.
            return false;
        }

        // Set null, not Remove: Remove fails on an absent path, so racing withdrawals would error.
        // Set is idempotent, and null reads back as no rating.
        var operation = rating is { } verdict
            ? PatchOperation.Set(
                "/feedback",
                new TranscriptMessageFeedback { Rating = verdict, DateModified = ratedAt })
            : PatchOperation.Set<TranscriptMessageFeedback?>("/feedback", null);

        var patched = await _transcriptStore.PatchItemAsync(
            messageId.ToString(), TranscriptPartition(userId, conversationId), [operation], cancellationToken);

        if (!patched)
        {
            // The document was there for the read and gone for the patch: it was removed out of band
            // in between. Reported as the 404 it now is, rather than as a write failure.
            LogMessageDisappearedBeforePatch(_logger, messageId, conversationId);

            throw new NotFoundException($"Message with id {messageId} not found");
        }

        return true;
    }

    /// <summary>
    /// Maintains the relational <see cref="MessageFeedback"/> row that mirrors the rating.
    /// </summary>
    /// <remarks>
    /// The broad catch is deliberate: the transcript already holds the rating and re-rating repairs this row;
    /// cancellation still answers 499. A failed save leaves the row tracked as Added, so nothing else may save
    /// in this request.
    /// </remarks>
    private async Task ProjectFeedbackAsync(
        Guid userId, Guid conversationId, Guid messageId, MessageFeedbackRatings? rating,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _ctx.MessageFeedback
                .FirstOrDefaultAsync(
                    x => x.ConversationId == conversationId && x.MessageId == messageId, cancellationToken);

            if (existing is null)
            {
                _ctx.Add(new MessageFeedback
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    UserId = userId,
                    MessageId = messageId,
                    Rating = rating
                });
            }
            else
            {
                existing.Rating = rating;
            }

            await _ctx.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFeedbackProjectionFailed(_logger, ex, rating, messageId, conversationId);
        }
    }

    /// <summary>
    /// Creates chat options for the stream and attaches the turn's tools: document retrieval when
    /// the conversation has documents, and the tools of any MCP servers the user selected, resolved
    /// through <see cref="IMcpToolProvider"/> (permission-checked, cached).
    /// </summary>
    /// <returns>
    /// The options and the leases backing them (null when not selected), which the caller keeps alive for
    /// the whole stream.
    /// </returns>
    /// <remarks>
    /// On a tool-less model an MCP selection fails loudly, since the user made it, while implicit document
    /// retrieval is dropped with a warning so conversations holding files keep working.
    /// </remarks>
    /// <exception cref="ValidationException">MCP servers were selected but <paramref name="model"/> does not support tools.</exception>
    /// <exception cref="NotFoundException">A selected server does not exist or is deactivated.</exception>
    /// <exception cref="ForbiddenException">The user lacks the permission for a selected server.</exception>
    private async Task<(ChatOptions ChatOptions, IMcpToolLeaseSet? ToolLeases, IFileAgentToolLease? FileAgentLease)> CreateChatOptionsAsync(
        Guid sessionId, ModelDto model, List<McpServerSelectionDto> mcps, string? projectInstructions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        ChatOptions chatOptions = new()
        {
            ModelId = model.DeploymentName,
            ConversationId = sessionId.ToString()
        };

        // Two channels: the property gates the Azure OpenAI client (set even when false; Foundry clears
        // Reasoning), and Reasoning reaches the Bedrock and Anthropic bridges. Reasoning must stay
        // conditional: the Responses adapter applies it with ??=, so on a non-reasoning turn it would
        // refill the raw options the Azure client left empty and the deployment would reject the request.
        if (model.IsReasoningEnabled)
        {
            chatOptions.Reasoning = new()
            {
                Effort = ReasoningEffort.High,
                Output = ReasoningOutput.Full
            };
        }

        chatOptions.AdditionalProperties ??= [];
        chatOptions.AdditionalProperties[ChatRequestProperties.IsReasoningEnabled] = model.IsReasoningEnabled;

        // Instructions ride the request, not the message list: each provider maps them to its own slot,
        // and the messages stay equal to the transcript.
        List<string> instructions = [];
        List<AITool> tools = [];

        if (!string.IsNullOrWhiteSpace(projectInstructions))
        {
            // Providers place Instructions differently, so the wrapper's wording, not message order, is what
            // ranks these below the platform prompt.
            instructions.Add(ConversationPrompts.BuildProjectInstructionsPrompt(projectInstructions));
        }

        // Resolved once and captured, so a mid-turn removal cannot change the corpus under a
        // half-answered question. A failure costs retrieval, not the turn: this runs for every conversation.
        DocumentRetrievalScope? documentScope = null;
        try
        {
            documentScope = await _documentRetrievalService
                .GetScopeAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolvingDocumentScopeFailed(_logger, ex, sessionId);
        }

        var attachDocumentTool = documentScope?.HasDocuments is true;
        if (attachDocumentTool && !model.IsToolEnabled)
        {
            // Logged rather than silent: without this line, a user asking about their PDF gets a
            // confident answer with no retrieval behind it and nothing explaining why.
            LogDocumentRetrievalNeedsTools(_logger, sessionId, documentScope!.Documents.Count, model.Id);

            attachDocumentTool = false;
        }

        // Gated beyond retrieval; the grant read is normally a cache hit written at sign-in.
        var attachSummaryTool = _summarizationOptions.Enabled && documentScope?.HasDocuments is true;

        if (attachSummaryTool && !model.IsToolEnabled)
        {
            LogDocumentSummarizationNeedsTools(_logger, sessionId, model.Id);

            attachSummaryTool = false;
        }

        if (attachSummaryTool)
        {
            var granted = await _userGrantReader
                .GetGrantsAsync(_tokenService.GetOid(), cancellationToken)
                .ConfigureAwait(false);

            if (!granted.Contains(PermissionIds.UploadFile))
            {
                attachSummaryTool = false;
            }
        }

        // Only with a spreadsheet: a tool that always finds nothing teaches the model to stop calling it.
        // No permission gate, since it spends nothing and a caller who can read chunks can read cells.
        var spreadsheetNames = documentScope is null
            ? []
            : documentScope.Documents
                .Where(d => DocumentRetrievalService.SpreadsheetExtensions.Contains(Path.GetExtension(d.Name)))
                .Select(d => d.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var attachSheetQueryTool = _sheetQueryOptions.Enabled && spreadsheetNames.Length > 0;

        if (attachSheetQueryTool && !model.IsToolEnabled)
        {
            LogSheetQueryNeedsTools(_logger, sessionId, model.Id);

            attachSheetQueryTool = false;
        }

        if (attachSheetQueryTool)
        {
            // Uploads older than the sheet tables have chunks but no rows; attaching over them would
            // name a workbook every call then denies exists.
            try
            {
                attachSheetQueryTool = await _sheetQueryService
                    .HasQueryableSheetsAsync(documentScope!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckingForQueryableSheetsFailed(_logger, ex, sessionId);

                attachSheetQueryTool = false;
            }
        }

        var selectedServerIds = mcps.Select(m => m.Id).Distinct().ToArray();
        if (selectedServerIds.Length > 0 && !model.IsToolEnabled)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(CreateConversationStreamActionDto.McpServers),
                    "The selected model does not support tools; remove the MCP server selection or choose a tool-enabled model.")
            ]);
        }

        IMcpToolLeaseSet? toolLeases = null;
        IFileAgentToolLease? fileAgentLease = null;
        if (selectedServerIds.Length > 0)
        {
            toolLeases = await _mcpToolProvider.AcquireToolsAsync(selectedServerIds, cancellationToken).ConfigureAwait(false);
            tools.AddRange(toolLeases.Tools);
        }

        // The leases are held from here and the caller can only dispose what is returned, so a throw
        // below must release them or their MCP clients are never disposed.
        try
        {
            // An MCP server "document" with a tool "search" yields this exact name, which OpenAI-shaped
            // providers reject as a duplicate. The explicit MCP selection wins; retrieval stands down.
            if (attachDocumentTool && tools.Any(t => string.Equals(t.Name, DocumentTool.ToolName, StringComparison.OrdinalIgnoreCase)))
            {
                LogDocumentRetrievalNameClash(_logger, sessionId, DocumentTool.ToolName);

                attachDocumentTool = false;
            }

            if (attachDocumentTool)
            {
                tools.Add(DocumentTool.Create(documentScope!, _documentRetrievalService, _logger));
                instructions.Add(ConversationPrompts.BuildDocumentRetrievalPrompt(DocumentTool.ToolName, documentScope!.DocumentNames));
            }

            // Gated like adding a document, not like reading one: summarizing spends money on a cache miss.
            if (attachSummaryTool
                && tools.Any(t => string.Equals(t.Name, DocumentSummaryTool.ToolName, StringComparison.OrdinalIgnoreCase)))
            {
                LogDocumentSummarizationNameClash(_logger, sessionId, DocumentSummaryTool.ToolName);

                attachSummaryTool = false;
            }

            // After retrieval settles: the summary prompt must never point at a tool not on the request.
            if (attachSummaryTool && !attachDocumentTool)
            {
                LogSummarizationStandsDown(_logger, sessionId);

                attachSummaryTool = false;
            }

            if (attachSummaryTool)
            {
                tools.Add(DocumentSummaryTool.Create(
                    documentScope!,
                    sessionId,
                    _documentSummaryService,
                    _summarizationOptions.ToolTimeoutSeconds,
                    _logger));

                instructions.Add(ConversationPrompts.BuildDocumentSummaryToolPrompt(
                    DocumentSummaryTool.ToolName, DocumentTool.ToolName));
            }

            if (attachSheetQueryTool
                && tools.Any(t => string.Equals(t.Name, SheetQueryTool.ToolName, StringComparison.OrdinalIgnoreCase)))
            {
                LogSheetQueryNameClash(_logger, sessionId, SheetQueryTool.ToolName);

                attachSheetQueryTool = false;
            }

            // Checked after retrieval has settled, for the same reason summarization is: the sheet
            // prompt names the retrieval tool and tells the model when to prefer one over the other.
            if (attachSheetQueryTool && !attachDocumentTool)
            {
                LogSheetQueryStandsDown(_logger, sessionId);

                attachSheetQueryTool = false;
            }

            if (attachSheetQueryTool)
            {
                tools.Add(SheetQueryTool.Create(
                    documentScope!,
                    _sheetQueryService,
                    _sheetQueryOptions.ToolTimeoutSeconds,
                    _logger));

                instructions.Add(ConversationPrompts.BuildSheetQueryToolPrompt(
                    SheetQueryTool.ToolName, DocumentTool.ToolName, spreadsheetNames));
            }

            // The same gate ladder document summarization climbs. A stood-down agent is not a
            // failed turn: the assistant is simply never told the capability exists.
            var attachFileAgent = _fileAgentOptions.Enabled;

            if (attachFileAgent && !model.IsToolEnabled)
            {
                LogFileAgentNeedsTools(_logger, model.Id);

                attachFileAgent = false;
            }

            if (attachFileAgent)
            {
                // Silently: a caller never offered it has nothing to be told. Admins are not implicitly
                // granted it, and reading per turn makes a revocation land on the next one.
                var granted = await _userGrantReader
                    .GetGrantsAsync(_tokenService.GetOid(), cancellationToken)
                    .ConfigureAwait(false);

                if (!granted.Contains(PermissionIds.GenerateFiles))
                {
                    attachFileAgent = false;
                }
            }

            if (attachFileAgent
                && tools.Any(t => string.Equals(t.Name, FileAgentToolNames.Agent, StringComparison.OrdinalIgnoreCase)))
            {
                // The user's explicit MCP selection wins, as it does for every implicit tool here.
                LogFileAgentNameClash(_logger, sessionId, FileAgentToolNames.Agent);

                attachFileAgent = false;
            }

            if (attachFileAgent)
            {
                try
                {
                    fileAgentLease = await _fileAgentToolProvider
                        .AcquireAsync(sessionId, _tokenService.GetOid(), cancellationToken)
                        .ConfigureAwait(false);

                    // After composition: the ceiling counts rows against the agent's pinned model,
                    // which only the lease knows.
                    var quota = await _fileAgentQuotaService
                        .CheckAsync(_tokenService.GetOid(), fileAgentLease.Model.ModelId, cancellationToken)
                        .ConfigureAwait(false);

                    if (quota.Allowed)
                    {
                        tools.Add(fileAgentLease.Tool);
                    }
                    else
                    {
                        await fileAgentLease.DisposeAsync().ConfigureAwait(false);
                        fileAgentLease = null;

                        // The one stand-down the assistant is told about: the user had this capability
                        // an hour ago, so silence would read as a fault.
                        instructions.Add(
                            $"File generation is unavailable for now. {quota.Explanation} If the user asks for a "
                                + "file, tell them the limit was reached and that it resets within a day.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A misconfigured agent costs the turn a capability, not the turn itself —
                    // the startup validator is where a broken registration is meant to surface.
                    LogComposingFileAgentFailed(_logger, ex, sessionId);

                    // Else the lease would put an agent the turn was never offered on its usage row.
                    if (fileAgentLease is not null)
                    {
                        await fileAgentLease.DisposeAsync().ConfigureAwait(false);
                        fileAgentLease = null;
                    }
                }
            }

            if (instructions.Count > 0)
            {
                chatOptions.Instructions = string.Join("\n\n", instructions);
            }

            // Assigned once. Assigning per source would have the last writer discard the others.
            if (tools.Count > 0)
            {
                chatOptions.Tools = tools;
                chatOptions.AllowMultipleToolCalls = true;
            }

            return (chatOptions, toolLeases, fileAgentLease);
        }
        catch when (toolLeases is not null || fileAgentLease is not null)
        {
            // Innermost first, matching how the stream's nested `await using` scopes unwind.
            if (toolLeases is not null)
            {
                await toolLeases.DisposeAsync().ConfigureAwait(false);
            }

            if (fileAgentLease is not null)
            {
                await fileAgentLease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation with id {Id} not found")]
    private static partial void LogConversationNotFound(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Committing conversation {ConversationId} failed after its transcript was written. Partition {PartitionKey} is orphaned and can be deleted.")]
    private static partial void LogConversationCommitFailed(
        ILogger logger, Exception exception, Guid conversationId, string partitionKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation with id {Id} not found or already deactivated")]
    private static partial void LogConversationNotFoundOrDeactivated(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No valid conversations found to deactivate.")]
    private static partial void LogNoValidConversationsFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation for id {Id} not found in Cosmos DB")]
    private static partial void LogConversationHeaderNotFound(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Model with id {Id} not found")]
    private static partial void LogModelNotFound(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to create name for chat id {Id}")]
    private static partial void LogConversationNamingFailed(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation not found or already deactivated")]
    private static partial void LogUpdatedConversationNotFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation with id {Id} not found.")]
    private static partial void LogStreamConversationNotFound(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disposing the response stream failed for conversation {ConversationId}.")]
    private static partial void LogDisposingResponseStreamFailed(
        ILogger logger, Exception exception, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to record usage for the abandoned turn on conversation {ConversationId}.")]
    private static partial void LogAbandonedTurnUsageFailed(ILogger logger, Exception exception, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No usage report for the {Status} turn on conversation {ConversationId}; recording it with no tokens.")]
    private static partial void LogNoUsageReportForTurn(
        ILogger logger, ConversationUsageStatuses status, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An agent tool call was reported for conversation {ConversationId} with no resolved agent model; its usage row carries no model.")]
    private static partial void LogAgentToolCallWithoutModel(ILogger logger, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the transcript for conversation {Id} failed ({Failure}); the completed turn was not persisted. Usage row {UsageId} references assistant message {AssistantMessageId}, which was never written.")]
    private static partial void LogTranscriptWriteFailed(
        ILogger logger, Guid id, string failure, Guid usageId, Guid assistantMessageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} not found, deactivated, or not owned by user {UserId}")]
    private static partial void LogConversationNotAccessible(ILogger logger, Guid conversationId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId} not found, deactivated, or not owned by user {UserId}")]
    private static partial void LogProjectNotAccessible(ILogger logger, Guid projectId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} is already being processed.")]
    private static partial void LogConversationBusy(ILogger logger, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trimmed {DroppedMessages} messages holding {DroppedTokens} tokens from the replayed history to fit {DeploymentName}.")]
    private static partial void LogHistoryTrimmed(
        ILogger logger, int droppedMessages, long droppedTokens, string deploymentName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {ModelId} ('{ModelName}') declares a context window of zero, so replayed history is not bounded for it. Set ContextWindowSize on the catalog row.")]
    private static partial void LogContextWindowUnbounded(ILogger logger, Guid modelId, string modelName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation {Id} not found.")]
    private static partial void LogTranscriptConversationNotFound(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {Id} has no transcript; returning an empty one. This is expected for a conversation created before the transcript cutover.")]
    private static partial void LogTranscriptMissing(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Estimating the token cost of a transcript message failed; recording it with no context cost.")]
    private static partial void LogTokenEstimationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} not found in conversation {ConversationId}")]
    private static partial void LogMessageNotFoundInConversation(ILogger logger, Guid messageId, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} of conversation {ConversationId} disappeared between the read and the feedback patch")]
    private static partial void LogMessageDisappearedBeforePatch(ILogger logger, Guid messageId, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to project feedback {Rating} for message {MessageId} of conversation {ConversationId}. The transcript holds the rating; this row is stale until the message is rated again.")]
    private static partial void LogFeedbackProjectionFailed(
        ILogger logger, Exception exception, MessageFeedbackRatings? rating, Guid messageId, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Resolving the document scope failed for conversation {ConversationId}; the turn runs without document retrieval.")]
    private static partial void LogResolvingDocumentScopeFailed(
        ILogger logger, Exception exception, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} has {DocumentCount} document(s) but model {ModelId} does not support tools; document retrieval is unavailable for this turn.")]
    private static partial void LogDocumentRetrievalNeedsTools(
        ILogger logger, Guid conversationId, int documentCount, Guid modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} has documents but model {ModelId} does not support tools; document summarization is unavailable for this turn.")]
    private static partial void LogDocumentSummarizationNeedsTools(ILogger logger, Guid conversationId, Guid modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} has spreadsheets but model {ModelId} does not support tools; the sheet query is unavailable for this turn.")]
    private static partial void LogSheetQueryNeedsTools(ILogger logger, Guid conversationId, Guid modelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Checking for queryable sheets failed for conversation {ConversationId}; the turn runs without the sheet query.")]
    private static partial void LogCheckingForQueryableSheetsFailed(
        ILogger logger, Exception exception, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An MCP tool selected for conversation {ConversationId} is already named '{ToolName}'; document retrieval is unavailable for this turn.")]
    private static partial void LogDocumentRetrievalNameClash(ILogger logger, Guid conversationId, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An MCP tool selected for conversation {ConversationId} is already named '{ToolName}'; document summarization is unavailable for this turn.")]
    private static partial void LogDocumentSummarizationNameClash(ILogger logger, Guid conversationId, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document retrieval is unavailable for conversation {ConversationId}; document summarization stands down with it.")]
    private static partial void LogSummarizationStandsDown(ILogger logger, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An MCP tool selected for conversation {ConversationId} is already named '{ToolName}'; the sheet query is unavailable for this turn.")]
    private static partial void LogSheetQueryNameClash(ILogger logger, Guid conversationId, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document retrieval is unavailable for conversation {ConversationId}; the sheet query stands down with it.")]
    private static partial void LogSheetQueryStandsDown(ILogger logger, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The File Agent is enabled but model {ModelId} does not support tools; it is unavailable for this turn.")]
    private static partial void LogFileAgentNeedsTools(ILogger logger, Guid modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An MCP tool selected for conversation {ConversationId} is already named '{ToolName}'; the File Agent is unavailable for this turn.")]
    private static partial void LogFileAgentNameClash(ILogger logger, Guid conversationId, string toolName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Composing the File Agent failed for conversation {ConversationId}; it is unavailable for this turn.")]
    private static partial void LogComposingFileAgentFailed(ILogger logger, Exception exception, Guid conversationId);

    #endregion
}
