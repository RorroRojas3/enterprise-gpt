using Andes.Extensions.AI;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Microsoft.Azure.Cosmos;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Conversation = Enterprise.Gpt.Entity.Conversation;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Mappers;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Rendering;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Service.Tokenization;
using Enterprise.Gpt.Service.Tool;
using Enterprise.Gpt.Service.Transcripts;

namespace Enterprise.Gpt.Service
{
    public interface IConversationService
    {
        /// <summary>
        /// The <c>metadata</c> key the <see cref="AssistantUiEventKind.Finished"/> frame carries the
        /// persisted assistant message's identifier under (US-1101).
        /// </summary>
        /// <remarks>
        /// An opaque identifier clients match verbatim, like the problem-type URIs — renaming it is
        /// a breaking API change.
        /// <para>
        /// Present only on a turn whose answer actually reached the transcript. A cancelled or
        /// faulted turn is sent no <see cref="AssistantUiEventKind.Finished"/> frame at all, and a
        /// completed turn whose transcript write failed is sent one carrying no <c>metadata</c>
        /// object — the serializer omits nulls, so the key is absent rather than empty either way.
        /// </para>
        /// </remarks>
        const string AssistantMessageIdMetadataKey = "assistantMessageId";

        /// <summary>
        /// Reads a single conversation, including the model and MCP servers its most recent turn ran
        /// with so a client can restore that selection.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The conversation.</returns>
        /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
        Task<ConversationDetailDto> GetConversationAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the active documents of one of the caller's conversations, most recently uploaded
        /// first.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The conversation's documents.</returns>
        /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
        Task<List<ConversationDocumentDto>> GetConversationDocumentsAsync(Guid id, CancellationToken cancellationToken = default);

        Task<ConversationDto> CreateConversationAsync(CreateConversationActionDto request, CancellationToken cancellationToken = default);

        Task DeactivateConversationAsync(Guid id, CancellationToken cancellationToken = default);

        Task DeactivateConversationsBulkAsync(DeactivateConversationsBulkActionDto request, CancellationToken cancellationToken = default);

        Task UpdateConversationNameAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Searches the caller's active conversations, newest first unless an order is requested.
        /// </summary>
        /// <param name="name">A substring to match against the conversation name, or <see langword="null"/> for no name filter.</param>
        /// <param name="skip">The number of conversations to skip.</param>
        /// <param name="take">The page size.</param>
        /// <param name="isFavorite">Restricts the results to favourites when <see langword="true"/>, to non-favourites when <see langword="false"/>, and applies no filter when <see langword="null"/>.</param>
        /// <param name="projectId">Restricts the results to one of the caller's projects, or <see langword="null"/> for no project filter.</param>
        /// <param name="sort">An optional field to order by — one of <see cref="Sorting.ConversationSortKeys.Supported"/>.</param>
        /// <param name="dir">An optional direction, <c>asc</c> or <c>desc</c>.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A page of conversations.</returns>
        /// <exception cref="NotFoundException"><paramref name="projectId"/> is not an active project of the caller.</exception>
        /// <exception cref="ValidationException"><paramref name="sort"/> or <paramref name="dir"/> names something this listing does not accept.</exception>
        Task<PaginatedResponseDto<ConversationDto>> SearchConversationsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, Guid? projectId = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default);

        Task<ConversationDto> UpdateConversationAsync(UpdateConversationActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks the caller's conversation as a favourite, or clears the mark.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="request">The requested state.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
        Task SetConversationFavoriteAsync(Guid id, SetConversationFavoriteActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs one chat turn and streams what the assistant does while it runs: answer text,
        /// reasoning, and an activity event for every tool, MCP call and agent it invokes.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="request">The prompt, the model to serve it, and the MCP servers to attach.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The turn's events, in the order they occurred.</returns>
        /// <remarks>
        /// Validation and the caller's identity are resolved eagerly, so a bad request faults before
        /// the sequence is produced and the caller can answer it with a normal error response.
        /// Everything else — the conversation lock, the transcript read, the model call — happens on
        /// the first move, which is why contention and a missing conversation surface there.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        /// <exception cref="ConversationBusyException">Another turn is already in flight for this conversation.</exception>
        /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
        IAsyncEnumerable<AssistantUiEvent> StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken);

        /// <summary>
        /// Reads a conversation's transcript, oldest message first.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="take">
        /// How many of the newest messages to return, clamped to 1–100. Omit it to return the whole
        /// transcript, which is the default so that a client written before paging existed is
        /// unaffected.
        /// </param>
        /// <param name="before">
        /// Return only messages written strictly before this moment, so a client can walk backwards
        /// through a long transcript. Omit it to start at the newest.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The conversation's name, dates, counters and messages. System messages are excluded.</returns>
        /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
        Task<ChatConversationDto> GetConversationMessagesAsync(
            Guid id, int? take, DateTimeOffset? before, CancellationToken cancellationToken);

        /// <summary>
        /// Records, replaces or withdraws the caller's rating of an assistant message (US-1102).
        /// </summary>
        /// <param name="conversationId">The unique identifier of the conversation.</param>
        /// <param name="messageId">The unique identifier of the assistant message being rated.</param>
        /// <param name="request">The verdict, or a null rating to withdraw one already recorded.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <remarks>
        /// The rating lands on the transcript message document, which is what the transcript
        /// response projects and therefore what survives a reload; the relational
        /// <c>MessageFeedback</c> row written after it is a projection for reporting, and failing to
        /// write it is logged rather than thrown. That order is deliberate — see the implementation.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        /// <exception cref="NotFoundException">
        /// The conversation is not an active conversation of the caller, or it holds no message with
        /// this identifier.
        /// </exception>
        /// <exception cref="ValidationException">The message is not an assistant message.</exception>
        Task SetMessageFeedbackAsync(
            Guid conversationId, Guid messageId, SetMessageFeedbackActionDto request,
            CancellationToken cancellationToken = default);
    }

    public class ConversationService(ILogger<ConversationService> logger,
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
        IValidator<CreateConversationActionDto> createChatValidator,
        IValidator<CreateConversationStreamActionDto> createChatStreamActionValidator,
        IValidator<DeactivateConversationsBulkActionDto> deactivateChatBulkValidator,
        IValidator<UpdateConversationActionDto> updateSessionValidator,
        IValidator<SetMessageFeedbackActionDto> setMessageFeedbackValidator,
        IOptions<WeatherToolOptions> weatherToolOptions,
        IDocumentSummaryService documentSummaryService,
        IUserGrantReader userGrantReader,
        IOptions<SummarizationOptions> summarizationOptions,
        EnterpriseGptDbContext ctx) : IConversationService
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

        // Logged once per model rather than per turn: an unset context window is a permanent
        // property of a catalog row, and a warning on every turn would bury the rest of the log.
        private readonly ConcurrentDictionary<Guid, bool> _unboundedModelsReported = new();
        private readonly IValidator<CreateConversationActionDto> _createChatValidator = createChatValidator;
        private readonly IValidator<CreateConversationStreamActionDto> _createChatStreamActionValidator = createChatStreamActionValidator;
        private readonly IValidator<DeactivateConversationsBulkActionDto> _deactivateChatBulkValidator = deactivateChatBulkValidator;
        private readonly IValidator<UpdateConversationActionDto> _updateChatValidator = updateSessionValidator;
        private readonly IValidator<SetMessageFeedbackActionDto> _setMessageFeedbackValidator = setMessageFeedbackValidator;
        private readonly WeatherToolOptions _weatherToolOptions = weatherToolOptions.Value;
        private readonly IDocumentSummaryService _documentSummaryService = documentSummaryService;
        private readonly IUserGrantReader _userGrantReader = userGrantReader;
        private readonly SummarizationOptions _summarizationOptions = summarizationOptions.Value;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        // The synthetic pair every turn's stream opens with, ahead of the first model event.
        // The reasoning text ends with a blank line because the package reducer accumulates
        // reasoning deltas verbatim: without the separator, a reasoning-capable model's first
        // real delta would concatenate run-on with this sentence.
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
                .Where(s => s.Id == id && s.UserId == userId && !s.DateDeactivated.HasValue)
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
            if (conversation == null)
            {
                _logger.LogError("Conversation with id {Id} not found", id);
                throw new NotFoundException($"Conversation with id {id} not found");
            }

            // The servers attached to the newest chat turn, so a client can restore the selection the
            // conversation was last run with. Naming calls are excluded — they never carry tools, so
            // a conversation's first turn would otherwise resolve to an empty selection.
            //
            // Narrowed to servers that are still active and still permitted, matching what
            // McpToolProvider.AcquireToolsAsync will accept: handing back a deactivated or revoked
            // server would have the client replay it into the next turn and take a 404 or a 403 it
            // cannot act on. The audit row keeps the historical selection either way.
            //
            // A second round trip rather than a correlated subquery on the projection above: taking
            // the newest turn's collection per conversation row translates to SQL APPLY, which not
            // every provider this model runs on can express.
            conversation.McpServerIds = await _ctx.ConversationUsage
                .AsNoTracking()
                .Where(u => u.ConversationId == id && u.Kind == ConversationUsageKinds.Chat)
                .OrderByDescending(u => u.DateCreated)
                .Take(1)
                .SelectMany(u => u.McpServers
                    .Where(m => !m.McpServer.DateDeactivated.HasValue
                        && m.McpServer.Permissions.Any(p => !p.DateDeactivated.HasValue
                            && p.UserPermissions.Any(up => up.UserId == userId && !up.DateDeactivated.HasValue)))
                    .Select(m => m.McpServerId))
                .ToListAsync(cancellationToken);

            return conversation;
        }

        /// <inheritdoc />
        public async Task<List<ConversationDocumentDto>> GetConversationDocumentsAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var userId = _tokenService.GetOid();
            await EnsureConversationExistsAsync(id, userId, cancellationToken);

            return await _ctx.ConversationDocuments
                .AsNoTracking()
                .Where(x => x.ConversationId == id && !x.DateDeactivated.HasValue)
                .OrderByDescending(x => x.DateCreated)
                .Select(ConversationDocumentMapper.MapToConversationDocumentDtoExpression)
                .ToListAsync(cancellationToken);
        }

        public async Task<ConversationDto> CreateConversationAsync(CreateConversationActionDto request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            _createChatValidator.ValidateAndThrow(request);

            var userId = _tokenService.GetOid();

            if (request.ProjectId.HasValue)
            {
                await EnsureProjectExistsAsync(request.ProjectId.Value, userId, cancellationToken);
            }

            // The Cosmos write is inside the transaction so a failure there rolls the relational row
            // back rather than leaving a conversation the user can open but never stream.
            await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);
            var date = DateTimeOffset.UtcNow;
            var newChat = new Conversation()
            {
                Name = "New Conversation",
                UserId = userId,
                ProjectId = request.ProjectId,
                DateCreated = date,
                DateModified = date
            };
            _ctx.Add(newChat);
            await _ctx.SaveChangesAsync(cancellationToken);

            // No model has been chosen yet, so there is no provider to calibrate for: the
            // uncalibrated default is the honest estimator here, and it is what the budget will
            // re-estimate against on the first turn anyway.
            var systemMessage = BuildSeededSystemMessage(
                userId, newChat.Id, date, _tokenEstimatorResolver.Default, deploymentName: null);

            // Both counters start from the same message. Seeding only the header would leave the
            // relational roll-up understated by the system prompt for the life of the conversation:
            // every later turn moves the two by the same delta, so the gap never closes. A second
            // save because the message needs the identifier the first one assigned; both are inside
            // the transaction already open.
            newChat.ContextTokens = systemMessage.Tokens;
            await _ctx.SaveChangesAsync(cancellationToken);

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
                // The Cosmos write is inside the transaction so a relational failure rolls the row
                // back — but it cannot roll back the documents, which are in another store. A commit
                // that fails here therefore leaves a transcript partition with no conversation row
                // behind it. Logged with the partition key because nothing else can find it
                // afterwards: no query reaches a conversation that SQL has no record of.
                _logger.LogError(
                    ex,
                    "Committing conversation {ConversationId} failed after its transcript was written. Partition {PartitionKey} is orphaned and can be deleted.",
                    newChat.Id, PartitionKeys.For(userId, newChat.Id));

                throw;
            }

            return newChat.MapToChatDto();
        }

        public async Task DeactivateConversationAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var userId = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            var conversationExists = await _ctx.Conversations
                .Where(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue)
                .AnyAsync(cancellationToken);

            if (!conversationExists)
            {
                _logger.LogWarning("Conversation with id {Id} not found or already deactivated", id);
                return;
            }

            // Patched rather than read-modify-replaced so a turn streaming concurrently cannot
            // have its appended messages reverted by this write. Returns false when the document
            // is absent, which is tolerated for the same reason the previous null check was.
            await _transcriptStore.PatchItemAsync(id.ToString(), TranscriptPartition(userId, id),
            [
                PatchOperation.Set("/dateDeactivated", date),
            ], cancellationToken);

            // Ahead of the chunks and the document itself, so a summary never outlives what it
            // summarizes: a live summary row under a deleted document is a fully readable account of
            // everything that document said.
            await _ctx.ConversationDocumentSummaries
                .Where(s => s.ConversationDocument.ConversationId == id && !s.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.DateDeactivated, date)
                    .SetProperty(x => x.DateModified, date),
                    cancellationToken);

            // Leaf first for the reason the summary goes first: a live cell row under a dead sheet is
            // the document's own data, still readable.
            await _ctx.ConversationDocumentSheetRows
                .Where(r => r.ConversationDocumentSheet.ConversationDocument.ConversationId == id && !r.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(r => r
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocumentSheetColumns
                .Where(c => c.ConversationDocumentSheet.ConversationDocument.ConversationId == id && !c.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(c => c
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocumentSheets
                .Where(s => s.ConversationDocument.ConversationId == id && !s.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocumentChunks
                .Where(c => c.ConversationDocument.ConversationId == id && !c.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(c => c
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocuments
                .Where(d => d.ConversationId == id && !d.DateDeactivated.HasValue)
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

            _deactivateChatBulkValidator.ValidateAndThrow(request);

            var userId = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            var conversationIds = await _ctx.Conversations
                .Where(x => request.ConversationIds.Contains(x.Id) && x.UserId == userId && !x.DateDeactivated.HasValue)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            if (conversationIds.Count == 0)
            {
                _logger.LogWarning("No valid conversations found to deactivate.");
                return;
            }

            // Patched by identifier rather than queried and replaced. Besides removing the
            // read-modify-write window, this drops a cross-partition query whose predicates
            // (c.UserId, c.DateDeactivated) did not match the documents' camelCase property names
            // and so never selected anything. SQL has already narrowed the ids to this user's.
            foreach (var conversationId in conversationIds)
            {
                await _transcriptStore.PatchItemAsync(
                    conversationId.ToString(), TranscriptPartition(userId, conversationId),
                [
                    PatchOperation.Set("/dateDeactivated", date),
                ], cancellationToken);
            }

            await _ctx.ConversationDocumentSummaries
                .Where(s => conversationIds.Contains(s.ConversationDocument.ConversationId) && !s.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.DateDeactivated, date)
                    .SetProperty(x => x.DateModified, date),
                    cancellationToken);

            await _ctx.ConversationDocumentSheetRows
                .Where(r => conversationIds.Contains(r.ConversationDocumentSheet.ConversationDocument.ConversationId) && !r.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(r => r
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocumentSheetColumns
                .Where(c => conversationIds.Contains(c.ConversationDocumentSheet.ConversationDocument.ConversationId) && !c.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(c => c
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocumentSheets
                .Where(s => conversationIds.Contains(s.ConversationDocument.ConversationId) && !s.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocumentChunks
                .Where(c => conversationIds.Contains(c.ConversationDocument.ConversationId) && !c.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(c => c
                    .SetProperty(x => x.DateDeactivated, date),
                    cancellationToken);

            await _ctx.ConversationDocuments
                .Where(d => conversationIds.Contains(d.ConversationId) && !d.DateDeactivated.HasValue)
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

            _createChatStreamActionValidator.ValidateAndThrow(request);

            // Read untracked: the write below is an ExecuteUpdate, and a tracked entity carrying the
            // pre-naming token values alongside it is exactly the mix EF warns about — a later
            // SaveChanges would write the stale counters back over the increment.
            // Owner-scoped and active-only, like every other read. The sole caller has already
            // matched ownership, but this method is public on the interface, and a filter that is
            // correct only because of who happens to call it is a filter waiting to be wrong.
            var callerId = _tokenService.GetOid();
            var conversation = await _ctx.Conversations
                .AsNoTracking()
                .Where(x => x.Id == id && x.UserId == callerId && !x.DateDeactivated.HasValue)
                .Select(x => new { x.Id, x.UserId })
                .FirstOrDefaultAsync(cancellationToken);
            if (conversation == null)
            {
                _logger.LogError("Conversation with id {Id} not found", id);
                throw new NotFoundException($"Conversation with id {id} not found");
            }

            var header = await ReadHeaderAsync(conversation.UserId, id, cancellationToken);
            if (header == null)
            {
                _logger.LogError("Conversation for id {Id} not found in Cosmos DB", id);
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
            // DeploymentName is not checked here: the create and update validators already enforce
            // NotEmpty on it, and guarding only this path — which runs on a conversation's first turn
            // alone — would leave every later turn shipping the same empty ModelId anyway.
            if (model is null)
            {
                _logger.LogError("Model with id {Id} not found", request.ModelId);
                throw new InvalidOperationException($"Model with id {request.ModelId} not found");
            }

            // Naming runs inline on a conversation's first turn, so it has to reach the same provider
            // as the turn itself — a deployment without an Azure client would otherwise fail here
            // before the stream ever produced a token.
            var chatClient = _chatClientResolver.Resolve(model.ProviderId);

            var response = await chatClient.GetResponseAsync([
                                 new ChatMessage(ChatRole.System, ConversationPrompts.BuildNamingPrompt()),
                                 new ChatMessage(ChatRole.User, $"Create a conversation name based on the following prompt, please make it 25 maximum and make it a string. Do not have the name on the conversation nor the id. Just the name based on the prompt. The result must be a string, not markdown. Prompt: {request.Prompt}")
                             ], new() { ModelId = model.DeploymentName }, cancellationToken);
            if (response == null)
            {
                _logger.LogError("Failed to create name for chat id {Id}", id);
                throw new InvalidOperationException($"Failed to create name for id {id}");
            }

            var name = response.Messages.Last().Text?.Trim() ?? string.Empty;
            var date = DateTimeOffset.UtcNow;

            // A non-streaming tracked request carries its report on the response rather than
            // in-band. Preferred over ChatResponse.Usage because it separates the assistant's own
            // consumption from anything a tool spent — although naming attaches no tools, so the
            // two agree today and the usage falls back to the raw response when tracking is not
            // installed on the client that served it.
            var report = response.AdditionalProperties?.TryGetValue(
                ToolTrackingChatClient.UsageReportPropertyName, out ChatUsageReport? usageReport) is true
                    ? usageReport
                    : null;

            var inputTokens = report?.AssistantUsage.InputTokenCount ?? response.Usage?.InputTokenCount ?? 0;
            var outputTokens = report?.AssistantUsage.OutputTokenCount ?? response.Usage?.OutputTokenCount ?? 0;

            // Read off the raw response rather than the report, which is the opposite of the two
            // above. The middleware's aggregation carries only input, output and total, so these
            // two arrive null on its report however faithfully the provider reported them; the
            // response's own usage is untouched and still has them. There is no per-turn breakdown
            // to fall back on here, because a non-streamed call produces none.
            var cachedInputTokens = response.Usage?.CachedInputTokenCount;
            var reasoningTokens = response.Usage?.ReasoningTokenCount;

            // Name and counters move together in one statement, and the usage row lands in the same
            // transaction, so a conversation whose totals include the naming call always carries the
            // row that accounts for it. Naming is a real completion against a real deployment and is
            // billed like any other call; it used to be charged to nobody, which made a
            // conversation's totals — and every report built on them — understate what was spent.
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
                // Priced like any other call: naming is a real completion against a real
                // deployment, so leaving it unpriced would understate a conversation's cost by
                // exactly the call that a report is least likely to look for.
                InputPricePerMillionTokens = model.InputPricePerMillionTokens,
                OutputPricePerMillionTokens = model.OutputPricePerMillionTokens,
                DateCreated = date
            });

            await _ctx.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Patch the two fields that changed. Passing the SQL entity to a full-document replace
            // here overwrote the Cosmos document with the relational shape and destroyed the
            // transcript — and this runs from inside a live stream, on the conversation's first turn.
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
            SortSelection<ConversationSortKey>? order = SortParameters.IsDefaultOrder(sort, dir)
                ? null
                : ConversationSortKeys.Resolve(sort, dir);

            // Clamped rather than validated: the paging arguments come straight off the query
            // string, and take = 0 would divide by zero when computing CurrentPage below.
            skip = Math.Max(skip, 0);
            take = Math.Clamp(take, 1, 100);

            var userId = _tokenService.GetOid();

            var query = _ctx.Conversations
                .AsNoTracking()
                .Where(x => x.UserId == userId && !x.DateDeactivated.HasValue);

            // Throws before the page is read, so a project that is missing, deactivated or another
            // user's answers 404 rather than an empty page — the route cannot be used to probe for
            // project ids, exactly as the single-project route behaves. A deactivated project is
            // the case that needs it most: DeactivateProjectAsync releases its conversations, so
            // without the check this would answer an empty page for a project that once had rows.
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
                .Select(ChatExtensions.MapToChatDtoExpression)
                .ToListAsync(cancellationToken);

            return new PaginatedResponseDto<ConversationDto>
            {
                Items = items,
                TotalCount = totalCount,
                PageSize = take,
                CurrentPage = (skip / take) + 1
            };
        }

        /// <remarks>
        /// The string comparison is the database collation's, not the CLR's — case-insensitive on a
        /// SQL Server default and ordinal on the SQLite the unit tests run against. A client must not
        /// re-sort a page with <c>localeCompare</c> and expect to agree with it.
        /// </remarks>
        private static IOrderedQueryable<Conversation> OrderConversations(
            IQueryable<Conversation> query,
            SortSelection<ConversationSortKey>? order)
        {
            if (order is not { } requested)
            {
                // Left exactly as it was before this listing accepted an order, tie-break and all —
                // which is to say without one. Adding Id here would reorder conversations created in
                // the same tick, and clients written against this route are promised it unchanged.
                return query.OrderByDescending(x => x.DateCreated);
            }

            var ordered = requested.Key switch
            {
                ConversationSortKey.Name => requested.IsAscending
                    ? query.OrderBy(x => x.Name)
                    : query.OrderByDescending(x => x.Name),
                _ => requested.IsAscending
                    ? query.OrderBy(x => x.DateCreated)
                    : query.OrderByDescending(x => x.DateCreated)
            };

            // Every requested order gets a tie-break the default cannot be given: a client paging
            // through a list needs the row at a page boundary to be the same row every time.
            //
            // It follows the requested direction rather than being fixed, and on this listing that
            // is the difference between a working control and an inert one: every untitled
            // conversation carries the same name, so under a fixed tie-break A-Z and Z-A would both
            // collapse to the tie-break's own order and the reader would see nothing happen.
            return requested.IsAscending
                ? ordered.ThenBy(x => x.Id)
                : ordered.ThenByDescending(x => x.Id);
        }

        public async Task<ConversationDto> UpdateConversationAsync(UpdateConversationActionDto request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            _updateChatValidator.ValidateAndThrow(request);

            var userId = _tokenService.GetOid();

            var anyConversation = await _ctx.Conversations
                .Where(x => x.Id == request.Id && x.UserId == userId && !x.DateDeactivated.HasValue)
                .AnyAsync(cancellationToken);
            if (!anyConversation)
            {
                _logger.LogWarning("Conversation not found or already deactivated");
                throw new NotFoundException($"Conversation not found or already deactivated.");
            }

            if (request.ProjectId.HasValue)
            {
                await EnsureProjectExistsAsync(request.ProjectId.Value, userId, cancellationToken);
            }

            var date = DateTimeOffset.UtcNow;
            // A full-representation PUT: an absent ProjectId removes the conversation from its
            // project rather than leaving the current value in place. The DTO documents this, since
            // a rename that forgets to echo the project id would otherwise silently orphan the
            // conversation.
            var rows = await _ctx.Conversations
                .Where(x => x.Id == request.Id && x.UserId == userId && !x.DateDeactivated.HasValue)
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

            // Re-read through the listing projection rather than GetConversationAsync: this route
            // returns ConversationDto, so the detail read's extra round trip for the MCP selection
            // would be spent on a field the response does not carry.
            return await _ctx.Conversations
                .AsNoTracking()
                .Where(x => x.Id == request.Id)
                .Select(ChatExtensions.MapToChatDtoExpression)
                .FirstAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task SetConversationFavoriteAsync(Guid id, SetConversationFavoriteActionDto request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var userId = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            // No Cosmos patch: favouriting only affects how conversations are listed, and the
            // transcript has no use for the flag. Keeping it out also keeps a star click off the
            // document a live turn may be appending to.
            // DateModified is deliberately left alone: starring a conversation does not modify it,
            // and moving the SQL timestamp would put it out of step with the Cosmos document's own,
            // which the transcript endpoint returns.
            var rows = await _ctx.Conversations
                .Where(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsFavorite, request.IsFavorite),
                    cancellationToken);

            if (rows == 0)
            {
                _logger.LogWarning("Conversation with id {Id} not found or already deactivated", id);
                throw new NotFoundException($"Conversation with id {id} not found");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Deliberately not an iterator method: in an <c>async IAsyncEnumerable</c> the whole body
        /// is deferred to the first <c>MoveNextAsync</c>, so the argument checks and validation
        /// would not run until the caller enumerated. Lock acquisition stays inside the iterator
        /// on purpose — hoisting it here would strand the lock for a caller that took the
        /// enumerable and never enumerated it — but it still happens before the first fragment is
        /// produced, which is what lets the controller answer contention with a clean 409.
        /// </remarks>
        public IAsyncEnumerable<AssistantUiEvent> StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            _createChatStreamActionValidator.ValidateAndThrow(request);

            var userId = _tokenService.GetOid();

            return StreamConversationCoreAsync(id, request, userId, cancellationToken);
        }

        /// <summary>
        /// Runs a single chat turn under the conversation lock: loads history, streams the model
        /// response and its tool activity to the caller, then persists the turn.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="request">The validated stream request.</param>
        /// <param name="userId">The object identifier of the calling user, resolved by the caller.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>An asynchronous sequence of events as the turn produces them.</returns>
        /// <exception cref="ConversationBusyException">Thrown when another turn is already in flight for this conversation.</exception>
        /// <exception cref="NotFoundException">Thrown when the conversation does not exist for this user.</exception>
        private async IAsyncEnumerable<AssistantUiEvent> StreamConversationCoreAsync(Guid id, CreateConversationStreamActionDto request, Guid userId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // using var, rather than a separate acquire and using block: it makes acquisition and
            // release inseparable, so no statement can be introduced between them that could throw
            // and strand the lock. A stranded conversation lock is unrecoverable short of a restart.
            using var lockReleaser = await _conversationLockService.TryAcquireLockAsync(id, cancellationToken)
                ?? throw LogAndCreateBusy(id);

            var conversation = await _ctx.Conversations
                            .AsNoTracking()
                            .SingleOrDefaultAsync(x => x.Id == id &&
                                x.UserId == userId &&
                                !x.DateDeactivated.HasValue, cancellationToken);
            if (conversation == null)
            {
                _logger.LogError("Conversation with id {Id} not found.", id);
                throw new NotFoundException($"Conversation with id {id} not found.");
            }

            // The moment the prompt was received, carried to the write so the user message is
            // stamped when it was asked rather than when the answer finished. Those are genuinely
            // different moments, and the difference is what makes dateCreated a total order.
            var promptReceivedAt = DateTimeOffset.UtcNow;

            // A one-RU point read rather than a count query. It answers two questions the turn needs
            // before it can run: whether this is the conversation's first turn, and whether the
            // header exists at all — a conversation created before the transcript cutover has none,
            // and patching a header that is absent would fail the whole write batch.
            var header = await ReadHeaderAsync(userId, id, cancellationToken);

            // Only the seeded system prompt so far, i.e. this is the conversation's first turn:
            // name it from the opening prompt before answering it.
            if (header is { MessageCount: 1 })
            {
                await UpdateConversationNameAsync(id, request, cancellationToken);
            }

            var transcript = await ReadMessagesAsync(userId, id, take: null, before: null, cancellationToken);

            var conversations = new List<ChatMessage>(transcript.Select(x => new ChatMessage(MappingService.MapToChatRole(x.Role), x.Content)))
            {
                new(ChatRole.User, request.Prompt)
            };

            // Read per turn rather than snapshotted into the transcript at creation time: editing a
            // project's instructions has to affect the conversations already in it, and moving a
            // conversation between projects has to swap which instructions apply. Carried on the
            // chat options rather than as a message for the same reason — the transcript is the
            // conversation's own record, and this is derived context that belongs to the request.
            var projectInstructions = await GetProjectInstructionsAsync(conversation.ProjectId, cancellationToken);

            var model = await _modelService.GetModelAsync(request.ModelId, cancellationToken);
            var chatClient = _chatClientResolver.Resolve(model.ProviderId);
            var (chatOptions, toolLeases) = await CreateChatOptionsAsync(id, model, request.McpServers, projectInstructions, cancellationToken).ConfigureAwait(false);

            StringBuilder sb = new();
            var completed = false;

            // Held back rather than forwarded with the rest of the stream. The frame has to carry
            // the identifier of the assistant message the turn produced, and that message does not
            // exist until PersistTurnAsync has written the transcript — which runs in the finally
            // below, after the loop that yields events has already exited. Re-emitted underneath
            // this block once the write has answered.
            AssistantUiEvent? finishedEvent = null;
            Guid? assistantMessageId = null;

            // Held for the same reason the frame is. A completed turn is owed its Finished event
            // whatever the write then does, and throwing out of the finally below would skip the
            // statement that delivers it — so the failure is captured there and rethrown after.
            ExceptionDispatchInfo? persistFailure = null;

            // The lease set (null when no MCPs are selected) must stay alive for the whole
            // stream: the attached tools invoke through the leased clients. await using
            // releases the leases on completion, fault, or client disconnect alike.
            await using (toolLeases)
            {
                // Inside the lease scope, not before it: this throws when the prompt cannot fit the
                // model's context window, and a throw between acquiring the leases and entering the
                // scope would strand them. A stranded lease never decrements its cache entry's
                // count, so the MCP client it holds is never disposed even after eviction.
                var estimator = _tokenEstimatorResolver.Resolve(model.ProviderId);
                var estimatedPromptTokens = ApplyContextBudget(
                    conversations, transcript, request.Prompt, model, chatOptions, estimator);

                // Collects the tracking middleware's report. Reading the token counts off the
                // stream instead would lose everything for a turn that never reaches its final
                // update, which is every cancelled turn.
                var usageScope = ChatUsageScope.Create();

                // Enumerated by hand rather than with await foreach, and this is the reason: the
                // scope has to be ambient while the middleware runs, and an async iterator resumes
                // on whatever execution context its consumer supplies, so anything made ambient
                // here is gone after the first yield return below. Re-entering the scope around
                // each move — and around the disposal that finalizes an abandoned request — is what
                // keeps it in effect for exactly the windows the middleware occupies.
                var events = chatClient
                    .GetStreamingResponseAsync(conversations, chatOptions, cancellationToken)
                    .ToUiEventsAsync(cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                // try/finally rather than straight-line code after the loop: a turn that is
                // cancelled or faults still consumed — and is still billed for — every token the
                // model produced up to that point, and this is the only path that runs when the
                // controller disposes the enumerator because the client disconnected. A catch
                // cannot enclose a yield return, so the outcome is classified from a flag.
                try
                {
                    // Synthesized ahead of the model call, after every failure point that can
                    // still answer with problem JSON — the lock, the conversation reads, naming,
                    // model resolution, and tool acquisition; the enumerator above is lazy, so a
                    // synchronous construction failure also stays pre-stream. The first yield
                    // commits the response to text/event-stream, which is a deliberate trade-off:
                    // a provider that faults on its very first token now surfaces as a truncated
                    // stream rather than a problem response, in exchange for the client hearing
                    // something before model latency. Inside the try, so a client that
                    // disconnects on these frames still reaches the finally that records the turn.
                    yield return new AssistantUiEvent
                    {
                        Kind = AssistantUiEventKind.Status,
                        Message = StartingStatusMessage,
                        Timestamp = DateTimeOffset.UtcNow
                    };

                    // A deliberate contract deviation, requested by the product: the package
                    // documents ReasoningDelta as model-sourced text, and this seeds it
                    // server-side, so every turn reports reasoning whatever the model streams.
                    // Timestamp stays at its default, as the contract prescribes for deltas.
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

                        // Only the answer is transcribed. Reasoning arrives as its own event kind
                        // and is shown live but never persisted: replaying a model's reasoning back
                        // to it on the next turn is neither useful nor what providers expect.
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
                    // Before the report is read: disposal is where the middleware finalizes a
                    // request that was abandoned rather than drained, and where it hands the
                    // observer the partial account of what that turn had already spent.
                    //
                    // Guarded for the same reason the audit write below is, and it needs its own
                    // guard because it runs ahead of that one: a provider aborting its response
                    // stream during teardown would otherwise replace the exception already in
                    // flight and skip the accounting entirely. Whatever the middleware published
                    // before the failure survives it.
                    try
                    {
                        using (usageScope.Enter())
                        {
                            await events.DisposeAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Disposing the response stream failed for conversation {ConversationId}.", id);
                    }

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
                        Report: usageScope.Report);

                    // The token that ended the stream is the one that would abort the write
                    // recording it, so finalization runs uncancellable.
                    try
                    {
                        assistantMessageId = await PersistTurnAsync(turn, CancellationToken.None);
                    }
                    catch (Exception ex) when (!completed)
                    {
                        // Filtered on !completed: throwing out of a finally while an exception is
                        // already in flight would replace it, so a cancelled turn would surface as a
                        // database error rather than the 499 it owes the client. Losing the usage row
                        // is the lesser harm.
                        _logger.LogError(ex, "Failed to record usage for the abandoned turn on conversation {ConversationId}.", id);
                    }
                    catch (Exception ex)
                    {
                        // The completed half. The failure still reaches the caller — captured rather
                        // than swallowed — but only after the Finished frame it would otherwise have
                        // cost. That frame used to be forwarded from inside the loop, so a write
                        // that threw truncated the body *after* it; holding the frame back must not
                        // quietly turn a completed turn into one the client reads as incomplete.
                        //
                        // It matters most for the failures after the SQL commit —
                        // _markdownRenderer.Render is one — which leave the same billed-but-not-
                        // transcribed state that a failed batch reports with a bare Finished.
                        persistFailure = ExceptionDispatchInfo.Capture(ex);
                    }
                }
            }

            // Outside the lease scope, because the leases have no part in this frame and their
            // lifetime belongs to the model call rather than to the write that followed it.
            //
            // Reached only on a turn that ran to completion: a cancelled turn and a faulted one are
            // never sent a Finished event by the middleware, and a client that disconnected stops
            // pulling — disposing a suspended iterator resumes it in dispose mode, which runs the
            // finally above and terminates rather than continuing here. That is what makes "no
            // assistantMessageId is claimed for an abandoned turn" structural rather than a
            // condition to remember.
            if (finishedEvent is not null)
            {
                // Null when the turn completed and the transcript write did not — the usage row is
                // committed but its message was never written, whether the batch reported failure
                // or the write threw. The frame still goes out, without the key: the identifier is
                // a promise about what the transcript will return, and there is nothing to return.
                //
                // Assignment rather than a merge, and safe because it is: ToUiEventsAsync documents
                // that it always leaves Metadata null, so there is nothing here to preserve. A
                // package that starts attaching its own values would need this to merge.
                yield return assistantMessageId is { } messageId
                    ? finishedEvent with
                    {
                        Metadata = new Dictionary<string, string>
                        {
                            [IConversationService.AssistantMessageIdMetadataKey] = messageId.ToString()
                        }
                    }
                    : finishedEvent;
            }

            // After the frame, never instead of it. Rethrown with its original stack, so the
            // endpoint still sees the failure — on a stream that has already started it short-
            // circuits on Response.HasStarted rather than corrupting the body.
            persistFailure?.Throw();
        }

        /// <summary>
        /// Everything a finished turn needs to record, whether it ran to completion or was abandoned.
        /// </summary>
        /// <param name="ConversationId">The conversation the turn belongs to.</param>
        /// <param name="UserId">The object identifier of the user the turn is charged to.</param>
        /// <param name="Model">The catalog model that served the turn.</param>
        /// <param name="McpServers">The MCP servers attached to the turn; empty when none were selected.</param>
        /// <param name="Status">How the turn ended.</param>
        /// <param name="Prompt">The user's prompt.</param>
        /// <param name="Answer">The text the model produced, possibly truncated when the turn was abandoned.</param>
        /// <param name="PromptReceivedAt">
        /// The moment the prompt arrived, which is what the user message is stamped with.
        /// </param>
        /// <param name="HeaderExists">
        /// Whether the conversation already has a transcript header. False for a conversation
        /// created before the transcript cutover, whose header the turn has to create rather than
        /// patch.
        /// </param>
        /// <param name="ConversationName">
        /// The conversation's name, needed only when the header has to be created.
        /// </param>
        /// <param name="ConversationCreatedAt">
        /// When the conversation was created, needed only when the header has to be created, so a
        /// reseeded header does not report this turn's moment as the conversation's creation date.
        /// </param>
        /// <param name="EstimatedPromptTokens">
        /// What this application estimated the prompt would cost, including per-turn overhead. Kept
        /// so finalization can compare it against what the provider reported.
        /// </param>
        /// <param name="Report">
        /// What the turn consumed, as the tool-tracking middleware accounted for it, or
        /// <see langword="null"/> when it produced no report at all.
        /// </param>
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
            ChatUsageReport? Report);

        /// <summary>
        /// Records a finished turn: the conversation's running counters and last model, the usage
        /// audit row with the tool calls underneath it, and — only for a turn that completed — the
        /// transcript append.
        /// </summary>
        /// <param name="turn">The outcome to record.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// The identifier of the assistant message this turn appended to the transcript, or
        /// <see langword="null"/> when it appended none — an abandoned turn, or a completed one
        /// whose transcript write failed after the usage row had already committed.
        /// </returns>
        /// <remarks>
        /// An abandoned turn is billed but not transcribed: appending a truncated answer would
        /// corrupt the history every later turn replays to the model. It still moves the counters
        /// and writes its usage row, so reports account for tokens the user stopped paying attention
        /// to but was still charged for.
        /// <para>
        /// The return value is deliberately narrower than the identifier written to
        /// <see cref="ConversationUsage.AssistantMessageId"/>, which is set before the transcript
        /// append is attempted and is therefore best-effort. What the caller puts on the wire has to
        /// be a message a client can actually read back.
        /// </para>
        /// </remarks>
        private async Task<Guid?> PersistTurnAsync(TurnOutcome turn, CancellationToken cancellationToken)
        {
            var date = DateTimeOffset.UtcNow;
            var completed = turn.Status == ConversationUsageStatuses.Completed;
            var assistantMessageId = Guid.NewGuid();
            var conversationId = turn.ConversationId;
            var modelId = turn.Model.Id;

            if (turn.Report is null)
            {
                // Tracking is installed on every chat client, so a missing report means the request
                // faulted before the middleware could account for anything. The turn is still
                // recorded — at zero — rather than dropped: a conversation with a gap in its audit
                // trail is worse than one with a zero-token row explaining the gap.
                _logger.LogWarning(
                    "No usage report for the {Status} turn on conversation {ConversationId}; recording it with no tokens.",
                    turn.Status, conversationId);
            }

            var turnUsage = UsageReportTranslator.Translate(turn.Report, turn.McpServers, date);

            // Only on a turn that invoked no tools. A turn that ran tools was billed for prompts the
            // estimator never saw — every tool round trip re-sends the conversation plus the tool's
            // result — so the two figures measure different things and comparing them would record
            // an error that is not one. Token-zero is deliberately not the test: an MCP server can
            // run and report no usage at all.
            if (turn.Report is not null && turnUsage.ToolCalls.Count == 0)
            {
                ChatMetrics.RecordEstimatorError(
                    turn.Model.ProviderId,
                    turn.Model.DeploymentName,
                    turn.EstimatedPromptTokens,
                    turnUsage.InputTokens);
            }

            // The conversation's running counters move by everything the turn consumed — the
            // assistant's own turns and every tool, MCP call and agent underneath them. Derived
            // from the same numbers written to the audit row below rather than read separately off
            // the report, so the counters can never disagree with the sum of the rows.
            var inputTokens = turnUsage.TotalInputTokens;
            var outputTokens = turnUsage.TotalOutputTokens;

            // Estimated before the transaction opens so the context roll-up can ride the update that
            // is already running, rather than costing a second statement. Null — not zero — for a
            // turn that was billed and never transcribed: null says "not transcribed" where zero
            // would claim it was transcribed and weighed nothing.
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

                // The turn's own two messages, which is what the audit row records. The reseeded
                // system message below is not one of them — it belongs to the conversation, not to
                // this turn — so it is deliberately outside this figure.
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

            // Id left unset so EF's sequential generator assigns it: this table grows by one row per
            // model call, and random GUIDs in its clustered key fragment the index that carries the
            // most rows in the schema.
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
                DateCreated = date,
                McpServers =
                [
                    .. turn.McpServers.Select(server => new ConversationUsageMcpServer
                    {
                        McpServerId = server.Id,
                        McpServerName = server.Name,
                        DateCreated = date
                    })
                ],
                // Saved as one graph rather than row by row: EF assigns the sequential keys and
                // fixes each child's ParentId from the navigation, so nesting survives without the
                // random identifiers the comment above exists to avoid.
                ToolCalls = turnUsage.ToolCalls,
                // Empty on a non-streamed call, whose provider reports one aggregate and no
                // breakdown. That is why a tool row's Iteration is only meaningful alongside these.
                Turns = turnUsage.Turns
            };

            _ctx.Add(usage);
            await _ctx.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (!completed)
            {
                return null;
            }

            // Clamped to at least one tick after the prompt. A turn that completes inside a single
            // tick would otherwise stamp both messages identically, and with no array position and
            // no sequence field to fall back on, ORDER BY dateCreated could render the answer before
            // the question — and replay it to the model that way on every later turn.
            var answeredAt = date > turn.PromptReceivedAt ? date : turn.PromptReceivedAt.AddTicks(1);

            // The deployment name, not the display label: the transcript is append-only, so the value
            // recorded here has to stay meaningful after a model is renamed in the catalog, and it is
            // the deployment that identifies which model actually served and billed the turn.
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
                }
            };

            // One batch, so the header's counters and the documents they count either all land or
            // none do. That atomicity is what makes messageCount exact rather than advisory, and it
            // is why nothing has to reconcile the two afterwards.
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
                        // A conversation created before the transcript cutover has no header, and
                        // patching one that is absent would fail the whole batch. Its earlier
                        // messages were destroyed at cutover and do not come back, but the seeded
                        // system message has to: it is the assistant's standing instructions, and a
                        // conversation that never reseeds it would run every future turn without
                        // them — silently, because nothing reports a missing system prompt.
                        batch.Create(reseededSystemMessage!.Id, reseededSystemMessage);

                        // Stamped with the conversation's real creation date, not this turn's. The
                        // transcript read serves the header's date once one exists, so stamping
                        // "now" would have the same conversation report two different creation dates
                        // either side of its first post-cutover turn.
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
                // The token counters and the usage row have already been committed to SQL, so failing
                // silently here would bill the turn and lose it. Nothing can be returned to the
                // caller at this point — the answer has been streamed — so this has to be caught in
                // the logs. Both identifiers are logged because the usage row now asserts an
                // AssistantMessageId that no transcript contains; reconciling needs the pair.
                _logger.LogError(
                    "Writing the transcript for conversation {Id} failed ({Failure}); the completed turn was not persisted. Usage row {UsageId} references assistant message {AssistantMessageId}, which was never written.",
                    conversationId, result.Describe(), usage.Id, assistantMessageId);

                return null;
            }

            return assistantMessageId;
        }

        /// <summary>
        /// Throws when the conversation does not exist, is deactivated, or belongs to another user.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="userId">The object identifier of the calling user.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <exception cref="NotFoundException">The conversation is not an active conversation of this user.</exception>
        private async Task EnsureConversationExistsAsync(Guid id, Guid userId, CancellationToken cancellationToken)
        {
            var exists = await _ctx.Conversations
                .AsNoTracking()
                .AnyAsync(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue, cancellationToken);

            if (!exists)
            {
                // Deliberately indistinguishable from "does not exist" so this cannot be used to
                // probe for conversation ids belonging to other users.
                _logger.LogWarning("Conversation {ConversationId} not found, deactivated, or not owned by user {UserId}", id, userId);
                throw new NotFoundException($"Conversation with id {id} not found");
            }
        }

        /// <summary>
        /// Throws when the project does not exist, is deactivated, or belongs to another user.
        /// </summary>
        /// <param name="projectId">The unique identifier of the project.</param>
        /// <param name="userId">The object identifier of the calling user.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <exception cref="NotFoundException">The project is not an active project of this user.</exception>
        private async Task EnsureProjectExistsAsync(Guid projectId, Guid userId, CancellationToken cancellationToken)
        {
            var exists = await _ctx.Projects
                .AsNoTracking()
                .AnyAsync(x => x.Id == projectId && x.UserId == userId && !x.DateDeactivated.HasValue, cancellationToken);

            if (!exists)
            {
                // Deliberately indistinguishable from "does not exist" so this cannot be used to
                // probe for project ids belonging to other users.
                _logger.LogWarning("Project {ProjectId} not found, deactivated, or not owned by user {UserId}", projectId, userId);
                throw new NotFoundException($"Project with id '{projectId}' was not found.");
            }
        }

        /// <summary>
        /// Reads the standing instructions of the conversation's project, if it has one.
        /// </summary>
        /// <param name="projectId">The conversation's project, or <see langword="null"/> when standalone.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// The project's instructions, or <see langword="null"/> when the conversation has no project,
        /// the project has been deactivated, or it carries no instructions.
        /// </returns>
        /// <remarks>
        /// Ownership is not re-checked here: the caller has already matched the conversation to the
        /// signed-in user, and a project can only ever hold that same user's conversations.
        /// </remarks>
        private async Task<string?> GetProjectInstructionsAsync(Guid? projectId, CancellationToken cancellationToken)
        {
            if (!projectId.HasValue)
            {
                return null;
            }

            return await _ctx.Projects
                .AsNoTracking()
                .Where(x => x.Id == projectId.Value && !x.DateDeactivated.HasValue)
                .Select(x => x.Instructions)
                .FirstOrDefaultAsync(cancellationToken);
        }

        /// <summary>
        /// Logs the contention and builds the exception to throw for a conversation that already
        /// has a turn in flight.
        /// </summary>
        /// <param name="id">The unique identifier of the contended conversation.</param>
        /// <returns>The exception to throw.</returns>
        private ConversationBusyException LogAndCreateBusy(Guid id)
        {
            _logger.LogWarning("Conversation {ConversationId} is already being processed.", id);

            return new ConversationBusyException($"Conversation {id} is currently being processed. Please wait for the current request to complete.");
        }

        /// <summary>
        /// Trims a turn's replayed history in place to what the selected model's context window can
        /// hold, and reports the estimated prompt that results.
        /// </summary>
        /// <param name="replay">The messages about to be sent, trimmed in place.</param>
        /// <param name="transcript">The stored messages backing all but the last entry of <paramref name="replay"/>.</param>
        /// <param name="prompt">The current prompt, which is the last entry of <paramref name="replay"/>.</param>
        /// <param name="model">The model that will answer.</param>
        /// <param name="chatOptions">The options the turn will be sent with, read for tools and instructions.</param>
        /// <param name="estimator">The estimator for the model's provider.</param>
        /// <returns>The estimated prompt in tokens, including the per-turn overhead.</returns>
        /// <exception cref="ValidationException">
        /// Thrown when the system message and the current prompt exceed the model's window on their
        /// own. Failing here is deliberate: the alternative is sending a prompt the provider will
        /// reject after the user has waited for it.
        /// </exception>
        /// <remarks>
        /// Trimming decides what is <em>sent</em>, never what is <em>stored</em> — the transcript
        /// keeps every message either way.
        /// </remarks>
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
                // A message stored before per-message accounting existed carries zero, which would
                // make it free to keep. Estimated on the fly instead, so the budget measures the
                // whole prompt rather than the part of it this application happened to have counted.
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

                _logger.LogInformation(
                    "Trimmed {DroppedMessages} messages holding {DroppedTokens} tokens from the replayed history to fit {DeploymentName}.",
                    budget.DroppedMessages, budget.DroppedTokens, model.DeploymentName);

                var kept = budget.KeptIndexes.Select(index => replay[index]).ToList();
                replay.Clear();
                replay.AddRange(kept);
            }

            return budget.KeptIndexes.Sum(index => budgetMessages[index].Tokens) + overhead;
        }

        /// <summary>
        /// Logs once per model that its catalog row declares no context window, so nothing is
        /// trimmed for it.
        /// </summary>
        /// <param name="model">The model with an unset context window.</param>
        private void LogUnboundedModelOnce(ModelDto model)
        {
            if (_unboundedModelsReported.TryAdd(model.Id, true))
            {
                _logger.LogWarning(
                    "Model {ModelId} ('{ModelName}') declares a context window of zero, so replayed history is not bounded for it. Set ContextWindowSize on the catalog row.",
                    model.Id, model.Name);
            }
        }

        /// <summary>
        /// Answers a transcript read for a conversation that has a relational row but no transcript.
        /// </summary>
        /// <param name="userId">The object identifier of the calling user.</param>
        /// <param name="id">The conversation's unique identifier.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The conversation with an empty message list.</returns>
        /// <exception cref="NotFoundException">
        /// Thrown when no active conversation with that identifier belongs to the caller — which is
        /// also what a foreign identifier produces, so the two are indistinguishable to a caller.
        /// </exception>
        /// <remarks>
        /// The only path that falls back to SQL. It exists for conversations that predate the
        /// transcript cutover: their rows survived and their transcripts did not, and a row a user
        /// can see in their sidebar has to open.
        /// </remarks>
        private async Task<ChatConversationDto> ReadEmptyTranscriptAsync(
            Guid userId, Guid id, CancellationToken cancellationToken)
        {
            var conversation = await _ctx.Conversations
                .AsNoTracking()
                .Where(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue)
                .Select(x => new { x.Name, x.DateCreated, x.DateModified })
                .FirstOrDefaultAsync(cancellationToken);

            if (conversation is null)
            {
                _logger.LogError("Conversation {Id} not found.", id);
                throw new NotFoundException($"Conversation {id} not found.");
            }

            _logger.LogInformation(
                "Conversation {Id} has no transcript; returning an empty one. This is expected for a conversation created before the transcript cutover.",
                id);

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
        /// <param name="userId">The object identifier of the conversation's owner.</param>
        /// <param name="conversationId">The conversation the message belongs to.</param>
        /// <param name="createdAt">
        /// The moment to stamp it with, which must precede every other message in the conversation.
        /// </param>
        /// <param name="estimator">The estimator for the serving provider.</param>
        /// <param name="deploymentName">The deployment the text will be sent to.</param>
        /// <returns>The seeded system message.</returns>
        /// <remarks>
        /// The system message is not privileged: it goes through the same estimator and the same
        /// renderer as user text, so the header's counters describe every message beneath them on
        /// one basis.
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
        /// <param name="estimator">The estimator for the serving provider.</param>
        /// <param name="text">The message text.</param>
        /// <param name="deploymentName">The deployment the text was or will be sent to.</param>
        /// <returns>The estimated token count, or zero when estimation failed.</returns>
        /// <remarks>
        /// A tokenizer fault must not lose a turn whose answer has already been streamed to the
        /// user. Zero is honest here — it is what the recorded cost is — and it is recoverable,
        /// because the count is a pure function of the stored text and can be recomputed later.
        /// </remarks>
        private long EstimateTokens(ITokenEstimator estimator, string? text, string? deploymentName)
        {
            try
            {
                return estimator.CountTokens(text, deploymentName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Estimating the token cost of a transcript message failed; recording it with no context cost.");

                return 0;
            }
        }

        /// <summary>
        /// Builds the partition every one of a conversation's transcript documents lives in.
        /// </summary>
        /// <param name="userId">The object identifier of the conversation's owner.</param>
        /// <param name="conversationId">The conversation's unique identifier.</param>
        /// <returns>The partition key.</returns>
        /// <remarks>
        /// The caller's user id is always the one composed in, never a value read from a document.
        /// That is what makes a foreign conversation id address a partition holding nothing rather
        /// than one the caller has to be filtered out of.
        /// </remarks>
        private static PartitionKey TranscriptPartition(Guid userId, Guid conversationId)
        {
            return new PartitionKey(PartitionKeys.For(userId, conversationId));
        }

        /// <summary>
        /// Reads a conversation's header document.
        /// </summary>
        /// <param name="userId">The object identifier of the conversation's owner.</param>
        /// <param name="conversationId">The conversation's unique identifier.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The header, or <see langword="null"/> when the conversation has no transcript.</returns>
        private Task<TranscriptHeaderDocument?> ReadHeaderAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
        {
            return _transcriptStore.ReadItemAsync<TranscriptHeaderDocument>(
                conversationId.ToString(), TranscriptPartition(userId, conversationId), cancellationToken);
        }

        /// <summary>
        /// Reads a conversation's message documents in the order they were written.
        /// </summary>
        /// <param name="userId">The object identifier of the conversation's owner.</param>
        /// <param name="conversationId">The conversation's unique identifier.</param>
        /// <param name="take">
        /// How many of the newest messages to return, or <see langword="null"/> for all of them.
        /// </param>
        /// <param name="before">
        /// Return only messages written strictly before this moment, or <see langword="null"/> to
        /// start at the newest.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The messages, oldest first.</returns>
        /// <remarks>
        /// <para>
        /// Always a single-partition query. The <c>type</c> predicate stays in place because the
        /// container is heterogeneous by design, and it is what stops a third document kind added
        /// later from silently entering a query written before it existed.
        /// </para>
        /// <para>
        /// Ordered by <c>dateCreated</c> alone rather than by a <c>(dateCreated, id)</c> pair: a
        /// multi-property <c>ORDER BY</c> requires a composite index in Cosmos, and the tie it would
        /// break cannot occur — a turn's own two messages are clamped a tick apart, and the
        /// conversation lock serializes turns. The materialized page is still ordered by id within a
        /// timestamp so the result is total whatever the store returns.
        /// </para>
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

                // Kept draining until the requested count is actually in hand. A page size is a
                // ceiling that Cosmos may return fewer than — a page can even come back empty with
                // more results behind it — so stopping at the first page would silently truncate a
                // transcript, and truncating history the model replays is not a visible failure.
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

        /// <inheritdoc />
        public async Task<ChatConversationDto> GetConversationMessagesAsync(
            Guid id, int? take, DateTimeOffset? before, CancellationToken cancellationToken)
        {
            var userId = _tokenService.GetOid();

            // The partition key is built from the caller's user id, so a conversation belonging to
            // someone else addresses a partition that holds nothing and this read answers null — a
            // 404, never a 403, matching every other conversation route.
            var header = await ReadHeaderAsync(userId, id, cancellationToken);

            if (header is null)
            {
                // A conversation created before the transcript cutover has a SQL row and no header,
                // because the cutover destroyed every transcript. It still opens: an empty
                // transcript is the truth about it, and an error would strand a row the user can
                // see in their sidebar.
                return await ReadEmptyTranscriptAsync(userId, id, cancellationToken);
            }

            if (header.DateDeactivated.HasValue)
            {
                _logger.LogError("Conversation {Id} not found.", id);
                throw new NotFoundException($"Conversation {id} not found.");
            }

            // Clamped rather than validated, matching every other paginated route here. Absent
            // means the whole transcript: the client that reads this endpoint today expects every
            // message, so paging is opt-in and the default is unchanged.
            var clamped = take.HasValue ? Math.Clamp(take.Value, 1, 100) : (int?)null;

            // One more than asked for, purely to answer "is there another page". Deriving that from
            // the header's total instead would be wrong while walking backwards: every page would be
            // compared against the whole transcript rather than against what is left behind it, so
            // the last page would always claim another one follows.
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
                                // Read straight off the document rather than joined from the usage
                                // row: the transcript recorded the turn's totals beside the message
                                // when it was written, and the audit row is the wrong side to reach
                                // from — it is keyed the other way and would cost a query per page.
                                Usage = x.Usage is null
                                    ? null
                                    : new ConversationMessageUsageDto
                                    {
                                        InputTokens = x.Usage.InputTokens,
                                        OutputTokens = x.Usage.OutputTokens
                                    },
                                // Read off the document for the same reason, and the reason it is
                                // written there first: this projection is the whole of what a reader
                                // gets back on a reload, so the relational row cannot stand in for it.
                                Feedback = x.Feedback is null
                                    ? null
                                    : new ConversationMessageFeedbackDto
                                    {
                                        Rating = x.Feedback.Rating,
                                        DateModified = x.Feedback.DateModified
                                    }
                            })
                            .ToList();

            return new()
            {
                Id = id,
                Name = header.Name,
                DateCreated = header.DateCreated,
                DateModified = header.DateModified,
                Messages = messages,
                // Taken from the header rather than counted, which the write batch's atomicity is
                // what makes exact. The system message is counted here and excluded from the
                // payload, because the total describes the transcript rather than this page of it —
                // so the oldest page of a paged read comes back one message short of `take`, once,
                // when it is the page the system message falls on.
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

            _setMessageFeedbackValidator.ValidateAndThrow(request);

            var userId = _tokenService.GetOid();

            // Establishes the 404 for an unknown, deactivated or foreign conversation, and satisfies
            // the projection row's foreign key before anything tries to write it.
            await EnsureConversationExistsAsync(conversationId, userId, cancellationToken);

            // A point read rather than a query, and no "does this message belong to that
            // conversation" check afterwards: the partition key composes the caller's user id with
            // the conversation id, so a message from another conversation — or another user's copy
            // of this one — addresses a partition that holds nothing and this answers null.
            var message = await _transcriptStore.ReadItemAsync<TranscriptMessageDocument>(
                messageId.ToString(), TranscriptPartition(userId, conversationId), cancellationToken);

            // The discriminator is checked here and not only in the query predicates, because this
            // is the one read in the service whose id comes straight off the route. The header
            // document shares the partition under the conversation's own id, so a caller passing the
            // conversation id as the message id would otherwise point-read the header, have it
            // deserialize into this shape with every unmapped member dropped, and be answered about
            // a message that does not exist.
            if (message is null || message.Type is not TranscriptDocumentTypes.Message)
            {
                _logger.LogWarning(
                    "Message {MessageId} not found in conversation {ConversationId}", messageId, conversationId);

                throw new NotFoundException($"Message with id {messageId} not found");
            }

            if (message.Role is not ChatRoles.Assistant)
            {
                // Keyed on the route parameter rather than a property of the body, as the sort and
                // export parameters are: the rating is well-formed, and the message it names is not
                // one there is anything to rate.
                throw new ValidationException(
                [
                    new ValidationFailure(
                        "messageId", "Feedback can only be recorded against an assistant message.")
                ]);
            }

            var ratedAt = DateTimeOffset.UtcNow;

            var written = await PatchTranscriptFeedbackAsync(
                message, userId, conversationId, messageId, request.Rating, ratedAt, cancellationToken);

            if (!written)
            {
                // Withdrawing a rating nobody recorded. Projecting it anyway would file a row
                // claiming this message was considered, for every message a client cared to post a
                // null rating at — and "messages considered" is the denominator reporting reads.
                return;
            }

            await ProjectFeedbackAsync(
                userId, conversationId, messageId, request.Rating, ratedAt, cancellationToken);
        }

        /// <summary>
        /// Writes the rating onto the transcript message document, which is what the transcript
        /// response reads back.
        /// </summary>
        /// <param name="message">The message document as it was read a moment ago.</param>
        /// <param name="userId">The object identifier of the conversation's owner.</param>
        /// <param name="conversationId">The conversation's unique identifier.</param>
        /// <param name="messageId">The message's unique identifier.</param>
        /// <param name="rating">The verdict, or <see langword="null"/> to withdraw one.</param>
        /// <param name="ratedAt">The moment to stamp on the rating.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="true"/> when the document was written; <see langword="false"/> when the
        /// rating asked for is the one it already holds and there was nothing to do.
        /// </returns>
        /// <exception cref="NotFoundException">The document was deleted between the read and the patch.</exception>
        private async Task<bool> PatchTranscriptFeedbackAsync(
            TranscriptMessageDocument message, Guid userId, Guid conversationId, Guid messageId,
            MessageFeedbackRatings? rating, DateTimeOffset ratedAt, CancellationToken cancellationToken)
        {
            if (rating is null && message.Feedback is null)
            {
                // Withdrawing a rating that was never recorded. The end state is already the one
                // being asked for, so nothing is written and nothing is charged. Safe to decide
                // from a read this time, unlike the branch below: skipping a write can only ever
                // leave the document as the caller found it.
                return false;
            }

            // Set to null rather than Remove, even though Remove is what "withdraw" sounds like.
            // Cosmos rejects Remove against a path the document does not carry, and two withdrawals
            // racing each other would both pass the read above and the loser would take a 400 the
            // caller can do nothing with — a 500 for a request whose work was already done. Set is
            // idempotent whatever the document currently holds, and a JSON null deserializes back
            // into a null Feedback, which the transcript projection reports as no rating at all.
            var operation = rating is { } verdict
                ? PatchOperation.Set(
                    "/feedback",
                    new TranscriptMessageFeedback { Rating = verdict, DateModified = ratedAt })
                : PatchOperation.Set<TranscriptMessageFeedback?>("/feedback", null);

            var patched = await _transcriptStore.PatchItemAsync(
                messageId.ToString(), TranscriptPartition(userId, conversationId), [operation], cancellationToken);

            if (!patched)
            {
                // The document was there for the read and gone for the patch: a purge landed in
                // between. Reported as the 404 it now is, rather than as a write failure.
                _logger.LogWarning(
                    "Message {MessageId} of conversation {ConversationId} disappeared between the read and the feedback patch",
                    messageId, conversationId);

                throw new NotFoundException($"Message with id {messageId} not found");
            }

            return true;
        }

        /// <summary>
        /// Maintains the relational <see cref="MessageFeedback"/> row that mirrors the rating.
        /// </summary>
        /// <param name="userId">The object identifier of the conversation's owner.</param>
        /// <param name="conversationId">The conversation's unique identifier.</param>
        /// <param name="messageId">The message's unique identifier.</param>
        /// <param name="rating">The verdict, or <see langword="null"/> to withdraw one.</param>
        /// <param name="ratedAt">The moment to stamp on the row.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <remarks>
        /// Never throws, and the breadth of the catch is the point rather than an oversight. Every
        /// 204 this route returns has to mean the reader's rating comes back on the next reload, and
        /// the transcript patch above is what decides that; this row exists so the same fact is
        /// queryable outside one conversation's partition. Failing the request here would revert a
        /// thumb over a rating that was in fact stored, and rating the message again repairs the row
        /// — so the failure is logged, loudly, and swallowed. The same trade
        /// <see cref="ConversationUsage.AssistantMessageId"/> documents, in the same direction.
        /// <para>
        /// Two consequences of that choice are deliberate. A failed save leaves the new entity
        /// tracked as <c>Added</c>; nothing saves again inside this request, so it dies with the
        /// scoped context. And cancellation is excluded from the catch, so a token cancelled between
        /// the two writes answers 499 for a rating the transcript has already stored — the caller
        /// asked to stop, and the alternative is reporting success on a request they abandoned.
        /// </para>
        /// </remarks>
        private async Task ProjectFeedbackAsync(
            Guid userId, Guid conversationId, Guid messageId, MessageFeedbackRatings? rating,
            DateTimeOffset ratedAt, CancellationToken cancellationToken)
        {
            try
            {
                var existing = await _ctx.MessageFeedback
                    .FirstOrDefaultAsync(
                        x => x.ConversationId == conversationId && x.MessageId == messageId, cancellationToken);

                if (existing is null)
                {
                    _ctx.MessageFeedback.Add(new MessageFeedback
                    {
                        Id = Guid.NewGuid(),
                        ConversationId = conversationId,
                        UserId = userId,
                        MessageId = messageId,
                        Rating = rating,
                        DateCreated = ratedAt,
                        DateModified = ratedAt
                    });
                }
                else
                {
                    existing.Rating = rating;
                    existing.DateModified = ratedAt;
                }

                await _ctx.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Failed to project feedback {Rating} for message {MessageId} of conversation {ConversationId}. The transcript holds the rating; this row is stale until the message is rated again.",
                    rating, messageId, conversationId);
            }
        }

        /// <summary>
        /// Creates chat options for the stream and attaches the turn's tools: document retrieval when
        /// the conversation has documents, and the tools of any MCP servers the user selected, resolved
        /// through <see cref="IMcpToolProvider"/> (permission-checked, cached).
        /// </summary>
        /// <param name="sessionId">The unique identifier of the chat session.</param>
        /// <param name="model">The AI model to use for the chat.</param>
        /// <param name="mcps">The MCP servers the user selected for tool calling.</param>
        /// <param name="projectInstructions">
        /// The standing instructions of the conversation's project, or <see langword="null"/> when it
        /// is standalone or its project sets none.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// The configured chat options and the tool lease set backing them, or
        /// <see langword="null"/> leases when no MCP servers were selected. The caller must
        /// keep the lease set alive for the whole stream and dispose it afterwards.
        /// </returns>
        /// <remarks>
        /// The two kinds of tool are treated differently on a model that cannot call tools. An MCP
        /// selection is something the user made and can undo, so it fails loudly; document retrieval is
        /// attached implicitly, and failing the turn over it would break every conversation that happens
        /// to hold a file, so it is dropped with a warning instead.
        /// </remarks>
        /// <exception cref="ValidationException">MCP servers were selected but <paramref name="model"/> does not support tools.</exception>
        /// <exception cref="NotFoundException">A selected server does not exist or is deactivated.</exception>
        /// <exception cref="ForbiddenException">The user lacks the permission for a selected server.</exception>
        private async Task<(ChatOptions ChatOptions, IMcpToolLeaseSet? ToolLeases)> CreateChatOptionsAsync(
            Guid sessionId, ModelDto model, List<McpServerSelectionDto> mcps, string? projectInstructions, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(model);

            ChatOptions chatOptions = new()
            {
                ModelId = model.DeploymentName,
                ConversationId = sessionId.ToString()
            };

            // The catalog's per-model reasoning flag, on two channels because they reach different
            // providers.
            //
            // The property is the gate the Azure OpenAI client reads, and it is set even when false
            // so that client can tell "this model does not reason" from "nobody said". The Azure AI
            // Foundry client — Chat Completions on the same resource — reads neither channel and
            // clears `Reasoning` outright, because reasoning is a Responses-API feature; that is
            // what makes a reasoning-flagged model pointed at that provider harmless rather than a
            // rejected request on every turn.
            //
            // `Reasoning` is Microsoft.Extensions.AI's own provider-agnostic request, which the
            // Bedrock and Anthropic bridges understand and which is the only reason this flag means
            // anything outside Azure. It is **conditional**, and that is load-bearing: the OpenAI
            // Responses adapter applies it with `??=`, so on a turn where the Azure client has
            // deliberately left the raw reasoning options empty — the non-reasoning path — an
            // unconditional value here fills them back in and the deployment rejects the whole
            // request. On a reasoning turn the Azure client sets its raw options first, so its
            // configured effort and summary win over these values.
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

            // Both blocks of instructions are derived context carried on the request rather than injected
            // into the message list: that is the channel each provider maps to its own instruction slot,
            // and it keeps the messages equal to the transcript instead of transcript-plus-derived-context.
            List<string> instructions = [];
            List<AITool> tools = [];

            if (!string.IsNullOrWhiteSpace(projectInstructions))
            {
                instructions.Add(ConversationPrompts.BuildProjectInstructionsPrompt(projectInstructions));
            }

            // Resolved once here, before the stream starts, and captured by the tool. Resolving per tool
            // call would repeat the query for every search the model runs, and would let a document
            // removed mid-turn change the corpus underneath a half-answered question.
            //
            // A failure to resolve it costs retrieval, not the turn: this now runs for every
            // conversation, including the ones with no documents at all, so letting it abort a turn
            // would make a feature nobody in that turn is using a new way for chat to fail.
            DocumentRetrievalScope? documentScope = null;
            try
            {
                documentScope = await _documentRetrievalService
                    .GetScopeAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Resolving the document scope failed for conversation {ConversationId}; the turn runs without document retrieval.", sessionId);
            }

            var attachDocumentTool = documentScope?.HasDocuments is true;
            if (attachDocumentTool && !model.IsToolEnabled)
            {
                // Logged rather than silent: without this line, a user asking about their PDF gets a
                // confident answer with no retrieval behind it and nothing explaining why.
                _logger.LogWarning(
                    "Conversation {ConversationId} has {DocumentCount} document(s) but model {ModelId} does not support tools; document retrieval is unavailable for this turn.",
                    sessionId, documentScope!.Documents.Count, model.Id);

                attachDocumentTool = false;
            }

            // Deliberately gated on more than retrieval is, and resolved here so the whole decision
            // sits beside the one it mirrors. A grant read is a cache hit in the normal case — the
            // entry is written at sign-in — so this costs nothing on a turn that is not the user's
            // first.
            var attachSummaryTool = _summarizationOptions.Enabled && documentScope?.HasDocuments is true;

            if (attachSummaryTool && !model.IsToolEnabled)
            {
                _logger.LogWarning(
                    "Conversation {ConversationId} has documents but model {ModelId} does not support tools; document summarization is unavailable for this turn.",
                    sessionId, model.Id);

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
            if (selectedServerIds.Length > 0)
            {
                toolLeases = await _mcpToolProvider.AcquireToolsAsync(selectedServerIds, cancellationToken).ConfigureAwait(false);
                tools.AddRange(toolLeases.Tools);
            }

            // Everything below runs with the leases already held, and the caller can only dispose
            // what this method returns. Anything that throws in between would strand them — and a
            // stranded lease never decrements its cache entry's count, so the MCP client it holds is
            // never disposed even after the entry is evicted.
            try
            {
                // MCP tools are named "{sanitizedServerName}_{tool}", so a server named "document" exposing a
                // tool named "search" produces exactly this name. Two identically named functions on one
                // request are rejected outright by OpenAI-shaped providers, and the usage audit would credit
                // retrieval's tokens to that server. The user's explicit MCP selection wins; retrieval is the
                // implicit one, so it stands down.
                if (attachDocumentTool && tools.Any(t => string.Equals(t.Name, DocumentTool.ToolName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning(
                        "An MCP tool selected for conversation {ConversationId} is already named '{ToolName}'; document retrieval is unavailable for this turn.",
                        sessionId, DocumentTool.ToolName);

                    attachDocumentTool = false;
                }

                if (attachDocumentTool)
                {
                    tools.Add(DocumentTool.Create(documentScope!, _documentRetrievalService, _logger));
                    instructions.Add(ConversationPrompts.BuildDocumentRetrievalPrompt(DocumentTool.ToolName, documentScope!.DocumentNames));
                }

                // Attached under strictly narrower conditions than retrieval: the feature has to be
                // switched on, and the caller has to hold the same grant that adding a document
                // needs. Reading a document back costs nothing and is gated on ownership alone;
                // summarizing spends money on a cache miss, so it is gated the way adding one is.
                if (attachSummaryTool
                    && tools.Any(t => string.Equals(t.Name, DocumentSummaryTool.ToolName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning(
                        "An MCP tool selected for conversation {ConversationId} is already named '{ToolName}'; document summarization is unavailable for this turn.",
                        sessionId, DocumentSummaryTool.ToolName);

                    attachSummaryTool = false;
                }

                // Checked after retrieval has settled, not beside the flag: retrieval stands down on
                // its own name collision, and a summarization prompt that tells the model to prefer
                // a tool which is not on the request is the one thing a tool prompt must never do.
                if (attachSummaryTool && !attachDocumentTool)
                {
                    _logger.LogWarning(
                        "Document retrieval is unavailable for conversation {ConversationId}; document summarization stands down with it.",
                        sessionId);

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

                // A fabricated tool, off in every environment that has not asked for it. It exists so the
                // activity timeline can be driven from a real turn — cards, sub-statuses, durations, and
                // the failed state — without standing up an MCP server. It stands down on a model that
                // cannot call tools for the same reason retrieval does: nothing about it is worth failing
                // a turn over.
                if (_weatherToolOptions.Enabled)
                {
                    if (model.IsToolEnabled)
                    {
                        tools.Add(WeatherTool.Create(_weatherToolOptions, _logger));
                    }
                    else
                    {
                        _logger.LogWarning(
                            "The weather tool is enabled but model {ModelId} does not support tools; it is unavailable for this turn.",
                            model.Id);
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

                return (chatOptions, toolLeases);
            }
            catch when (toolLeases is not null)
            {
                await toolLeases.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
