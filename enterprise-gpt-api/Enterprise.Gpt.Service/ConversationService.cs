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
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Microsoft.Azure.Cosmos;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Conversation = Enterprise.Gpt.Entity.Conversation;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Rendering;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Enterprise.Gpt.Service.Tool;

namespace Enterprise.Gpt.Service
{
    public interface IConversationService
    {
        /// <summary>
        /// Reads a single conversation, including the model and MCP servers its most recent turn ran
        /// with so a client can restore that selection.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The conversation.</returns>
        /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
        Task<ConversationDetailDto> GetConversationAsync(Guid id, CancellationToken cancellationToken = default);

        Task<ConversationDto> CreateConversationAsync(CreateConversationActionDto request, CancellationToken cancellationToken = default);

        Task DeactivateConversationAsync(Guid id, CancellationToken cancellationToken = default);

        Task DeactivateConversationsBulkAsync(DeactivateConversationsBulkActionDto request, CancellationToken cancellationToken = default);

        Task UpdateConversationNameAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Searches the caller's active conversations, newest first.
        /// </summary>
        /// <param name="name">A substring to match against the conversation name, or <see langword="null"/> for no name filter.</param>
        /// <param name="skip">The number of conversations to skip.</param>
        /// <param name="take">The page size.</param>
        /// <param name="isFavorite">Restricts the results to favourites when <see langword="true"/>, to non-favourites when <see langword="false"/>, and applies no filter when <see langword="null"/>.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A page of conversations.</returns>
        Task<PaginatedResponseDto<ConversationDto>> SearchConversationsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, CancellationToken cancellationToken = default);

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
        /// Reads a page of a conversation's transcript, newest messages last.
        /// </summary>
        /// <param name="id">The conversation to read.</param>
        /// <param name="take">How many messages to return, clamped server-side to 1–100.</param>
        /// <param name="before">
        /// Return the page immediately preceding this sequence, or <see langword="null"/> for the
        /// newest page.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>The conversation, its total message count, and one page of messages in ascending sequence order.</returns>
        /// <exception cref="NotFoundException">The conversation is not a conversation of the caller.</exception>
        /// <remarks>
        /// System messages are never returned. The page is taken from the newest end because that
        /// is what a client opening a conversation shows first; older pages are walked backwards
        /// with <paramref name="before"/>.
        /// </remarks>
        Task<ChatConversationDto> GetConversationMessagesAsync(Guid id, int take, long? before, CancellationToken cancellationToken);
    }

    public class ConversationService(ILogger<ConversationService> logger,
        IModelService modelService,
        IChatClientResolver chatClientResolver,
        IMcpToolProvider mcpToolProvider,
        IDocumentRetrievalService documentRetrievalService,
        IConversationLockService conversationLockService,
        ITokenService tokenService,
        IAzureCosmosService cosmosService,
        ITokenEstimatorResolver tokenEstimatorResolver,
        IMarkdownRenderer markdownRenderer,
        IPromptEstimator promptEstimator,
        IContextBudgetCalculator contextBudgetCalculator,
        IOptions<ContextBudgetOptions> contextBudgetOptions,
        ChatMetrics chatMetrics,
        IValidator<CreateConversationActionDto> createChatValidator,
        IValidator<CreateConversationStreamActionDto> createChatStreamActionValidator,
        IValidator<DeactivateConversationsBulkActionDto> deactivateChatBulkValidator,
        IValidator<UpdateConversationActionDto> updateSessionValidator,
        EnterpriseGptDbContext ctx) : IConversationService
    {
        private readonly ILogger _logger = logger;
        private readonly IChatClientResolver _chatClientResolver = chatClientResolver;
        private readonly IModelService _modelService = modelService;
        private readonly IMcpToolProvider _mcpToolProvider = mcpToolProvider;
        private readonly IDocumentRetrievalService _documentRetrievalService = documentRetrievalService;
        private readonly IConversationLockService _conversationLockService = conversationLockService;
        private readonly ITokenEstimatorResolver _tokenEstimatorResolver = tokenEstimatorResolver;
        private readonly IMarkdownRenderer _markdownRenderer = markdownRenderer;
        private readonly IPromptEstimator _promptEstimator = promptEstimator;
        private readonly IContextBudgetCalculator _contextBudgetCalculator = contextBudgetCalculator;
        private readonly IOptions<ContextBudgetOptions> _contextBudgetOptions = contextBudgetOptions;
        private readonly ChatMetrics _chatMetrics = chatMetrics;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IAzureCosmosService _cosmosService = cosmosService;
        private readonly IValidator<CreateConversationActionDto> _createChatValidator = createChatValidator;
        private readonly IValidator<CreateConversationStreamActionDto> _createChatStreamActionValidator = createChatStreamActionValidator;
        private readonly IValidator<DeactivateConversationsBulkActionDto> _deactivateChatBulkValidator = deactivateChatBulkValidator;
        private readonly IValidator<UpdateConversationActionDto> _updateChatValidator = updateSessionValidator;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        // The synthetic pair every turn's stream opens with, ahead of the first model event.
        // The reasoning text ends with a blank line because the package reducer accumulates
        // reasoning deltas verbatim: without the separator, a reasoning-capable model's first
        // real delta would concatenate run-on with this sentence.
        /// <summary>
        /// How many message documents a transcript read fetches per round trip.
        /// </summary>
        /// <remarks>
        /// Sized so an ordinary conversation is read in one request while a very long one still
        /// pages rather than materializing unboundedly in a single response.
        /// </remarks>
        private const int TranscriptPageSize = 200;

        /// <summary>
        /// The smallest page the transcript route will serve.
        /// </summary>
        private const int MinTranscriptPageTake = 1;

        /// <summary>
        /// The largest page the transcript route will serve, and its default.
        /// </summary>
        private const int MaxTranscriptPageTake = 100;

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

            var prompt = ConversationPrompts.BuildDefaultSystemPrompt();
            var header = new CosmosConversationHeader
            {
                Id = newChat.Id,
                UserId = userId,
                ConversationId = newChat.Id,
                Name = newChat.Name,
                TotalTokens = 0,
                // The seeded system message occupies position 0, so the next turn allocates from 1.
                MessageCount = 1,
                DateCreated = date,
                DateModified = date
            };

            var systemMessage = BuildMessage(newChat.Id, userId, ChatRoles.System, prompt, sequence: 0, date);

            // Header and seeded message in one batch: a header whose counter says a message exists
            // must not be readable before the message it counts.
            await _cosmosService.ExecuteBatchAsync(userId, newChat.Id,
            [
                CosmosBatchOperation.CreateItem(header),
                CosmosBatchOperation.CreateItem(systemMessage)
            ], cancellationToken);

            await transaction.CommitAsync(cancellationToken);

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

            // The header alone: soft-deleting a conversation hides it, and leaving the message
            // documents in place is what keeps that reversible. Patched rather than
            // read-modify-replaced so a turn streaming concurrently cannot have its counter
            // updates reverted. A null return means the header is absent, which is tolerated for
            // the same reason the previous null check was.
            await _cosmosService.PatchItemAsync<CosmosConversationHeader>(id.ToString(), userId, id,
            [
                PatchOperation.Set("/dateDeactivated", date),
            ], cancellationToken);

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
                await _cosmosService.PatchItemAsync<CosmosConversationHeader>(conversationId.ToString(), userId, conversationId,
                [
                    PatchOperation.Set("/dateDeactivated", date),
                ], cancellationToken);
            }

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
            var conversation = await _ctx.Conversations
                .AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.UserId })
                .FirstOrDefaultAsync(cancellationToken);
            if (conversation == null)
            {
                _logger.LogError("Conversation with id {Id} not found", id);
                throw new NotFoundException($"Conversation with id {id} not found");
            }

            var cosmosConversation = await _cosmosService.GetItemAsync<CosmosConversationHeader>(id.ToString(), conversation.UserId, id, cancellationToken);
            if (cosmosConversation is null)
            {
                _logger.LogError("Conversation for id {Id} not found in Cosmos DB", id);
                throw new NotFoundException($"Conversation for id {id} not found");
            }

            var model = await _ctx.Models
                        .AsNoTracking()
                        .Where(x => x.Id == request.ModelId && !x.DateDeactivated.HasValue)
                        .Select(x => new { x.DeploymentName, x.ProviderId, x.InputPricePerMillionTokens, x.OutputPricePerMillionTokens })
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
                InputPricePerMillionTokens = model.InputPricePerMillionTokens,
                OutputPricePerMillionTokens = model.OutputPricePerMillionTokens,
                DateCreated = date
            });

            await _ctx.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Patch the two fields that changed on the header, touching no message document.
            // Passing the SQL entity to a full-document replace here once overwrote the Cosmos
            // document with the relational shape and destroyed the transcript — and this runs from
            // inside a live stream, on the conversation's first turn.
            await _cosmosService.PatchItemAsync<CosmosConversationHeader>(conversation.Id.ToString(), conversation.UserId, conversation.Id,
            [
                PatchOperation.Set("/name", name),
                PatchOperation.Set("/dateModified", date),
            ], cancellationToken);
        }

        /// <inheritdoc />
        public async Task<PaginatedResponseDto<ConversationDto>> SearchConversationsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, CancellationToken cancellationToken = default)
        {
            // Clamped rather than validated: the paging arguments come straight off the query
            // string, and take = 0 would divide by zero when computing CurrentPage below.
            skip = Math.Max(skip, 0);
            take = Math.Clamp(take, 1, 100);

            var userId = _tokenService.GetOid();

            var query = _ctx.Conversations
                .AsNoTracking()
                .Where(x => x.UserId == userId && !x.DateDeactivated.HasValue);

            if (!string.IsNullOrWhiteSpace(name))
            {
                query = query.Where(x => !string.IsNullOrWhiteSpace(x.Name) && EF.Functions.Like(x.Name, $"%{name}%"));
            }

            if (isFavorite.HasValue)
            {
                query = query.Where(x => x.IsFavorite == isFavorite.Value);
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(x => x.DateCreated)
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

            await _cosmosService.PatchItemAsync<CosmosConversationHeader>(request.Id.ToString(), userId, request.Id,
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

            var header = await _cosmosService.GetItemAsync<CosmosConversationHeader>(id.ToString(), userId, id, cancellationToken);
            if (header is null)
            {
                _logger.LogError("Conversation {Id} not found.", id);
                throw new NotFoundException($"Conversation {id} not found.");
            }

            // Only the seeded system prompt has been allocated a position, i.e. this is the
            // conversation's first turn: name it from the opening prompt before answering it.
            if (header.MessageCount == 1)
            {
                await UpdateConversationNameAsync(id, request, cancellationToken);
            }

            var transcript = await ReadTranscriptAsync(id, userId, cancellationToken);

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

            // The lease set (null when no MCPs are selected) must stay alive for the whole
            // stream: the attached tools invoke through the leased clients. await using
            // releases the leases on completion, fault, or client disconnect alike.
            //
            // Nothing that can throw may sit between acquiring the leases and entering this block.
            // A leaked lease is not merely untidy: releasing one is what decrements the cached MCP
            // client's lease count, so an unreleased lease pins that client and its transport for
            // the life of the process — and an over-budget prompt would make that user-triggerable.
            await using (toolLeases)
            {
                // Trimmed here rather than before the block, because the overhead term depends on
                // how many tools the options attach and on the instructions they carry — both of
                // which cost prompt tokens that belong to no message — and because this throws for
                // a prompt that cannot fit.
                var (conversations, estimatedPromptTokens) = BuildReplay(model, transcript, request.Prompt, chatOptions);

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
                        EstimatedPromptTokens: estimatedPromptTokens,
                        Report: usageScope.Report);

                    // The token that ended the stream is the one that would abort the write
                    // recording it, so finalization runs uncancellable.
                    try
                    {
                        await PersistTurnAsync(turn, CancellationToken.None);
                    }
                    catch (Exception ex) when (!completed)
                    {
                        // Filtered on !completed: throwing out of a finally while an exception is
                        // already in flight would replace it, so a cancelled turn would surface as a
                        // database error rather than the 499 it owes the client. Losing the usage row
                        // is the lesser harm. A turn that completed has no exception to mask, so its
                        // failures still propagate.
                        _logger.LogError(ex, "Failed to record usage for the abandoned turn on conversation {ConversationId}.", id);
                    }
                }
            }
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
        /// <param name="EstimatedPromptTokens">
        /// What the prompt was estimated to cost, or <see langword="null"/> when budgeting was off
        /// and no estimate was produced. Compared against the provider's own figure where the two
        /// measure the same thing.
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
            long? EstimatedPromptTokens,
            ChatUsageReport? Report);

        /// <summary>
        /// Records a finished turn: the conversation's running counters and last model, the usage
        /// audit row with the tool calls underneath it, and — only for a turn that completed — the
        /// transcript append.
        /// </summary>
        /// <param name="turn">The outcome to record.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <remarks>
        /// An abandoned turn is billed but not transcribed: appending a truncated answer would
        /// corrupt the history every later turn replays to the model. It still moves the counters
        /// and writes its usage row, so reports account for tokens the user stopped paying attention
        /// to but was still charged for.
        /// </remarks>
        private async Task PersistTurnAsync(TurnOutcome turn, CancellationToken cancellationToken)
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

            RecordEstimateAccuracy(turn, turnUsage, completed);

            // The conversation's running counters move by everything the turn consumed — the
            // assistant's own turns and every tool, MCP call and agent underneath them. Derived
            // from the same numbers written to the audit row below rather than read separately off
            // the report, so the counters can never disagree with the sum of the rows.
            var inputTokens = turnUsage.TotalInputTokens;
            var outputTokens = turnUsage.TotalOutputTokens;

            // One transaction, so a conversation whose counters moved always carries the usage row
            // that accounts for the movement.
            await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

            await _ctx.Conversations
                .Where(s => s.Id == conversationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.InputTokens, x => x.InputTokens + inputTokens)
                    .SetProperty(x => x.OutputTokens, x => x.OutputTokens + outputTokens)
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
                InputPricePerMillionTokens = turn.Model.InputPricePerMillionTokens,
                OutputPricePerMillionTokens = turn.Model.OutputPricePerMillionTokens,
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
                ToolCalls = turnUsage.ToolCalls
            };

            _ctx.Add(usage);
            await _ctx.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (!completed)
            {
                return;
            }

            // Two positions claimed in one server-side increment, which is what makes ordering
            // independent of the conversation lock: that lock is process-local, so two replicas
            // answering the same conversation would otherwise be free to claim the same position.
            // The patched header comes back, so the value read here is the one this turn owns.
            var header = await _cosmosService.PatchItemAsync<CosmosConversationHeader>(
                turn.ConversationId.ToString(), turn.UserId, turn.ConversationId,
                [PatchOperation.Increment("/messageCount", 2)], cancellationToken);

            if (header is null)
            {
                // The token counters and the usage row have already been committed to SQL, so failing
                // silently here would bill the turn and lose it. Nothing can be returned to the
                // caller at this point — the answer has been streamed — so this has to be caught in
                // the logs. Both identifiers are logged because the usage row now asserts an
                // AssistantMessageId that no transcript contains; reconciling needs the pair.
                _logger.LogError(
                    "Conversation {Id} header is missing; the completed turn was not persisted. Usage row {UsageId} references assistant message {AssistantMessageId}, which was never written.",
                    turn.ConversationId, usage.Id, assistantMessageId);

                return;
            }

            var assistantSequence = header.MessageCount - 1;
            var userSequence = header.MessageCount - 2;

            // The deployment name, not the display label: the transcript is append-only, so the value
            // recorded here has to stay meaningful after a model is renamed in the catalog, and it is
            // the deployment that identifies which model actually served and billed the turn.
            var userMessage = BuildMessage(turn.ConversationId, turn.UserId, ChatRoles.User, turn.Prompt, userSequence, date, turn.Model);
            var assistantMessage = BuildMessage(turn.ConversationId, turn.UserId, ChatRoles.Assistant, turn.Answer, assistantSequence, date, turn.Model);
            assistantMessage.Id = assistantMessageId;
            assistantMessage.Usage = new CosmosConversationUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };

            try
            {
                // One transaction, so a header whose totals moved always has the messages that moved
                // them. Patching rather than replacing keeps a rename or a soft delete that landed
                // mid-stream from being reverted.
                await _cosmosService.ExecuteBatchAsync(turn.UserId, turn.ConversationId,
                [
                    CosmosBatchOperation.CreateItem(userMessage),
                    CosmosBatchOperation.CreateItem(assistantMessage),
                    CosmosBatchOperation.PatchItem(turn.ConversationId.ToString(),
                    [
                        PatchOperation.Increment("/totalTokens", inputTokens + outputTokens),
                        PatchOperation.Set("/dateModified", date)
                    ])
                ], cancellationToken);
            }
            catch (Exception ex) when (ex is CosmosBatchFailedException or CosmosException)
            {
                // Same reasoning as the missing header above: the answer has already streamed and
                // SQL has already committed, so the pair of identifiers in the log is the only way
                // to reconcile a usage row against a transcript that never received its message.
                _logger.LogError(
                    ex,
                    "Writing the transcript for conversation {Id} failed. Usage row {UsageId} references assistant message {AssistantMessageId}, which was never written.",
                    turn.ConversationId, usage.Id, assistantMessageId);
            }
        }

        /// <summary>
        /// Builds a transcript message document.
        /// </summary>
        /// <param name="conversationId">The conversation the message belongs to.</param>
        /// <param name="userId">The conversation's owner, the first partition key level.</param>
        /// <param name="role">Who produced the message.</param>
        /// <param name="content">The message text.</param>
        /// <param name="sequence">The position allocated to this message.</param>
        /// <param name="date">The timestamp to stamp on the message.</param>
        /// <param name="model">The model that served the turn, or <see langword="null"/> for the seeded system message.</param>
        /// <returns>The document, ready to write.</returns>
        private CosmosConversationMessage BuildMessage(
            Guid conversationId,
            Guid userId,
            ChatRoles role,
            string content,
            long sequence,
            DateTimeOffset date,
            ModelDto? model = null)
        {
            var (tokens, accuracy) = EstimateMessageTokens(model, content);

            return new CosmosConversationMessage
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ConversationId = conversationId,
                Sequence = sequence,
                Role = MappingService.MapToRoleName(role),
                Content = content,
                HtmlContent = RenderHtml(content),
                Tokens = tokens,
                TokenAccuracy = accuracy,
                DateCreated = date,
                Model = model?.DeploymentName,
                ProviderId = model?.ProviderId
            };
        }

        /// <summary>
        /// Compares the estimated prompt against what the provider reported, where the two measure
        /// the same thing.
        /// </summary>
        /// <param name="turn">The finished turn.</param>
        /// <param name="turnUsage">What the tool-tracking middleware accounted for.</param>
        /// <param name="completed">Whether the turn ran to completion.</param>
        /// <remarks>
        /// Only on turns with no tool calls. The provider's reported input tokens then cover the
        /// same prompt the estimator saw; once a tool runs, that figure also includes the follow-up
        /// prompts carrying tool results, which the estimator never counted and never should. Pure
        /// arithmetic over numbers already in hand, so it adds no round trip to the turn.
        /// </remarks>
        private void RecordEstimateAccuracy(TurnOutcome turn, TurnUsage turnUsage, bool completed)
        {
            if (!completed
                || turn.Report is null
                || turnUsage.ToolCalls.Count > 0
                || turn.EstimatedPromptTokens is not long estimate
                || turnUsage.InputTokens <= 0)
            {
                return;
            }

            var relativeError = (estimate - turnUsage.InputTokens) / (double)turnUsage.InputTokens;

            _chatMetrics.RecordEstimateRelativeError(relativeError, turn.Model.ProviderId, turn.Model.DeploymentName);
        }

        /// <summary>
        /// Counts what a message will cost as context on every later turn.
        /// </summary>
        /// <param name="model">The model that served the turn, or <see langword="null"/> for the seeded system message.</param>
        /// <param name="content">The message text.</param>
        /// <returns>The count and how it was arrived at.</returns>
        /// <remarks>
        /// A tokenizer fault returns zero rather than propagating: the answer has already been
        /// streamed by the time this runs, so losing the turn over a token count would trade
        /// something valuable for something cosmetic.
        /// </remarks>
        private (long Tokens, TokenAccuracies Accuracy) EstimateMessageTokens(ModelDto? model, string content)
        {
            try
            {
                // A null model means the seeded system message, written before a turn has chosen
                // one. The text is provider-agnostic English, so the fallback encoding's small
                // disagreement with the eventual provider's tokenizer is noise.
                var estimator = _tokenEstimatorResolver.Resolve(model?.ProviderId ?? Guid.Empty);

                return (estimator.CountTokens(model?.DeploymentName, content), estimator.Accuracy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Estimating the context cost of a message failed; recording it as zero.");

                return (0, TokenAccuracies.Estimated);
            }
        }

        /// <summary>
        /// Renders a message to the HTML stored beside it.
        /// </summary>
        /// <param name="content">The message text.</param>
        /// <returns>The rendered HTML, or the escaped plain text when rendering fails.</returns>
        /// <remarks>
        /// A renderer fault falls back to escaped text rather than propagating, for the same reason
        /// the estimator does: this runs after the answer has streamed, and a stored artifact is
        /// worth less than the turn that produced it.
        /// </remarks>
        private string RenderHtml(string content)
        {
            try
            {
                return _markdownRenderer.Render(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rendering a message to HTML failed; storing the escaped plain text instead.");

                return WebUtility.HtmlEncode(content);
            }
        }

        /// <summary>
        /// Builds the message list sent to the model, trimmed to fit its context window.
        /// </summary>
        /// <param name="model">The model that will answer the turn.</param>
        /// <param name="transcript">Every stored message, oldest first.</param>
        /// <param name="prompt">The question being asked.</param>
        /// <param name="chatOptions">The turn's options, whose tools and instructions cost prompt tokens.</param>
        /// <returns>The messages to send and what they were estimated to cost.</returns>
        /// <exception cref="ValidationException">
        /// Thrown when the standing instructions and the current prompt alone exceed the model's
        /// window, since no amount of trimming can make that turn sendable.
        /// </exception>
        /// <remarks>
        /// Decides what is sent, never what is stored: the transcript keeps every message whatever
        /// this drops.
        /// </remarks>
        private (List<ChatMessage> Messages, long? EstimatedPromptTokens) BuildReplay(
            ModelDto model,
            List<CosmosConversationMessage> transcript,
            string prompt,
            ChatOptions chatOptions)
        {
            var roles = transcript.Select(x => MappingService.MapToChatRoles(x.Role)).ToList();
            var contents = transcript.Select(x => x.Content).ToList();
            roles.Add(ChatRoles.User);
            contents.Add(prompt);

            List<ChatMessage> ToChatMessages(IEnumerable<int> positions) =>
                [.. positions.Select(position => new ChatMessage(MappingService.MapToChatRole(roles[position]), contents[position]))];

            if (!_contextBudgetOptions.Value.Enabled)
            {
                // The back-out: unbounded replay, exactly as it behaved before budgeting existed.
                return (ToChatMessages(Enumerable.Range(0, contents.Count)), null);
            }

            var candidates = transcript
                .Select(x => ((string? Content, long StoredTokens))(x.Content, x.Tokens))
                .Append((prompt, 0L))
                .ToList();

            var perMessage = _promptEstimator.EstimateMessages(model, candidates);
            var overhead = _promptEstimator.EstimateOverhead(model, chatOptions.Tools?.Count ?? 0, chatOptions.Instructions, candidates.Count);

            List<BudgetMessage> budgetMessages = [.. perMessage.Select((tokens, position) => new BudgetMessage(position, roles[position], tokens))];
            var result = _contextBudgetCalculator.Apply(model, budgetMessages, overhead);

            if (result.ExceedsBudget)
            {
                throw new ValidationException(
                [
                    new ValidationFailure(
                        nameof(CreateConversationStreamActionDto.Prompt),
                        $"This prompt does not fit the selected model's context window of {model.ContextWindowSize:F0} tokens. Shorten it or choose a model with a larger window.")
                ]);
            }

            if (result.DroppedCount > 0)
            {
                _chatMetrics.RecordBudgetDrop(result.DroppedCount, result.DroppedTokens, model.DeploymentName);

                _logger.LogInformation(
                    "Trimmed {DroppedCount} messages holding {DroppedTokens} tokens from the replay for model {DeploymentName} to fit a budget of {Budget}.",
                    result.DroppedCount,
                    result.DroppedTokens,
                    model.DeploymentName,
                    result.Budget);
            }

            var keptTokens = result.Kept.Sum(message => message.Tokens);

            return (ToChatMessages(result.Kept.Select(message => message.Index)), keptTokens + overhead);
        }

        /// <summary>
        /// Reads a conversation's messages in the order they happened.
        /// </summary>
        /// <param name="conversationId">The conversation to read.</param>
        /// <param name="userId">The conversation's owner, the first partition key level.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>Every message of the conversation, ordered by ascending sequence.</returns>
        /// <remarks>
        /// Prefix-routed to a single subpartition by the complete partition key, so this never fans
        /// out across the container however many conversations it holds.
        /// </remarks>
        private async Task<List<CosmosConversationMessage>> ReadTranscriptAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
        {
            var query = new QueryDefinition(
                    $"SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId ORDER BY c.sequence")
                .WithParameter("@type", CosmosTranscriptTypes.Message)
                .WithParameter("@conversationId", conversationId);

            var messages = new List<CosmosConversationMessage>();
            string? continuationToken = null;

            do
            {
                var page = await _cosmosService.QueryPageAsync<CosmosConversationMessage>(
                    query, userId, conversationId, TranscriptPageSize, continuationToken, cancellationToken);

                messages.AddRange(page.Items);
                continuationToken = page.ContinuationToken;
            }
            while (continuationToken is not null);

            return messages;
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

        /// <inheritdoc />
        public async Task<ChatConversationDto> GetConversationMessagesAsync(Guid id, int take, long? before, CancellationToken cancellationToken)
        {
            var userId = _tokenService.GetOid();

            // Existence and ownership come from SQL, not from the transcript, so the two can be
            // told apart: a conversation the caller does not own is a 404, but a conversation that
            // exists with no transcript is an empty conversation. That distinction is what a
            // cutover leaves behind — every conversation created before it keeps its SQL row and
            // loses its messages — and reporting those as missing would present a whole history as
            // broken rather than as emptied. A foreign or deleted id is a 404 rather than a 403,
            // matching every other conversation route.
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

            var header = await _cosmosService.GetItemAsync<CosmosConversationHeader>(id.ToString(), userId, id, cancellationToken);
            if (header is null)
            {
                _logger.LogWarning(
                    "Conversation {Id} has no transcript; returning it empty. This is expected for a conversation created before the transcript cutover.",
                    id);

                return new()
                {
                    Id = id,
                    Name = conversation.Name,
                    DateCreated = conversation.DateCreated,
                    DateModified = conversation.DateModified,
                    TotalMessageCount = 0,
                    HasMore = false,
                    Messages = []
                };
            }

            // A soft delete sets this on the header while the SQL row is updated in the same flow,
            // so it is belt and braces against the two disagreeing mid-operation.
            if (header.DateDeactivated.HasValue)
            {
                _logger.LogError("Conversation {Id} not found.", id);
                throw new NotFoundException($"Conversation {id} not found.");
            }

            // Clamped rather than validated, matching every other paginated list route here: the
            // paging arguments come off the query string, where an out-of-range value is a client
            // mistake to absorb rather than a request to reject.
            take = Math.Clamp(take, MinTranscriptPageTake, MaxTranscriptPageTake);

            // Newest first so a page can be taken off the tail, then reversed for display. The
            // system message is excluded server-side: it is the largest fixed message in a
            // conversation and no client renders it.
            var sql = before.HasValue
                ? "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId AND c.role != @systemRole AND c.sequence < @before ORDER BY c.sequence DESC"
                : "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId AND c.role != @systemRole ORDER BY c.sequence DESC";

            var query = new QueryDefinition(sql)
                .WithParameter("@type", CosmosTranscriptTypes.Message)
                .WithParameter("@conversationId", id)
                .WithParameter("@systemRole", MappingService.MapToRoleName(ChatRoles.System));

            if (before.HasValue)
            {
                query = query.WithParameter("@before", before.Value);
            }

            var page = await _cosmosService.QueryPageAsync<CosmosConversationMessage>(
                query, userId, id, take, null, cancellationToken);

            var messages = page.Items
                .Reverse()
                .Select(x => new ConversationMessageDto
                {
                    Sequence = x.Sequence,
                    Text = x.Content ?? string.Empty,
                    Role = MappingService.MapToChatRoles(x.Role),
                    HtmlContent = x.HtmlContent,
                    Tokens = x.Tokens,
                    TokenAccuracy = x.TokenAccuracy
                })
                .ToList();

            return new()
            {
                Id = id,
                Name = header.Name,
                DateCreated = header.DateCreated,
                DateModified = header.DateModified,
                TotalMessageCount = header.MessageCount,
                HasMore = page.ContinuationToken is not null,
                Messages = messages
            };
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
    }
}
