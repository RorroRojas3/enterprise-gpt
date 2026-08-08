using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Microsoft.Azure.Cosmos;
using System.Runtime.CompilerServices;
using System.Text;
using Conversation = Enterprise.Gpt.Entity.Conversation;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Prompts;

namespace Enterprise.Gpt.Service
{
    public interface IConversationService
    {
        Task<ConversationDto> GetConversationAsync(Guid id, CancellationToken cancellationToken = default);

        Task<ConversationDto> CreateConversationAsync(CreateConversationActionDto request, CancellationToken cancellationToken = default);

        Task DeactivateConversationAsync(Guid id, CancellationToken cancellationToken = default);

        Task DeactivateConversationsBulkAsync(DeactivateConversationsBulkActionDto request, CancellationToken cancellationToken = default);

        Task UpdateConversationNameAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken = default);

        Task<PaginatedResponseDto<ConversationDto>> SearchConversationsAsync(string? name, int skip = 0, int take = 20, CancellationToken cancellationToken = default);

        Task<ConversationDto> UpdateConversationAsync(UpdateConversationActionDto request, CancellationToken cancellationToken = default);

        IAsyncEnumerable<string?> StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken);

        Task<ChatConversationDto> GetConversationMessagesAsync(Guid id, CancellationToken cancellationToken);
    }

    public class ConversationService(ILogger<ConversationService> logger,
        IModelService modelService,
        IChatClientResolver chatClientResolver,
        IMcpToolProvider mcpToolProvider,
        IConversationLockService conversationLockService,
        ITokenService tokenService,
        IAzureCosmosService cosmosService,
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
        private readonly IConversationLockService _conversationLockService = conversationLockService;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IAzureCosmosService _cosmosService = cosmosService;
        private readonly IValidator<CreateConversationActionDto> _createChatValidator = createChatValidator;
        private readonly IValidator<CreateConversationStreamActionDto> _createChatStreamActionValidator = createChatStreamActionValidator;
        private readonly IValidator<DeactivateConversationsBulkActionDto> _deactivateChatBulkValidator = deactivateChatBulkValidator;
        private readonly IValidator<UpdateConversationActionDto> _updateChatValidator = updateSessionValidator;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        public async Task<ConversationDto> GetConversationAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var userId = _tokenService.GetOid();

            var conversation = await _ctx.Conversations
                .AsNoTracking()
                .Where(s => s.Id == id && s.UserId == userId && !s.DateDeactivated.HasValue)
                .Select(s => s.MapToChatDto())
                .FirstOrDefaultAsync(cancellationToken);
            if (conversation == null)
            {
                _logger.LogError("Conversation with id {Id} not found", id);
                throw new NotFoundException($"Conversation with id {id} not found");
            }

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
            var newCosmosChat = new CosmosConversation()
            {
                Id = newChat.Id,
                UserId = userId,
                Name = newChat.Name,
                TotalTokens = 0,
                DateCreated = date,
                DateModified = date,
                Documents = [],
                Messages = [new()
                {
                    Id = Guid.NewGuid(),
                    Role = ChatRoles.System,
                    Content = prompt,
                    DateCreated = date,
                }]
            };

            await _cosmosService.CreateItemAsync(newCosmosChat, userId.ToString(), cancellationToken);

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

            // Patched rather than read-modify-replaced so a turn streaming concurrently cannot
            // have its appended messages reverted by this write. Returns false when the document
            // is absent, which is tolerated for the same reason the previous null check was.
            await _cosmosService.PatchItemAsync(id.ToString(), userId.ToString(),
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
                .Where(x => request.ChatIds.Contains(x.Id) && x.UserId == userId && !x.DateDeactivated.HasValue)
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
                await _cosmosService.PatchItemAsync(conversationId.ToString(), userId.ToString(),
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

            var conversation = await _ctx.Conversations.FindAsync([id], cancellationToken);
            if (conversation == null)
            {
                _logger.LogError("Conversation with id {Id} not found", id);
                throw new NotFoundException($"Conversation with id {id} not found");
            }

            var cosmosConversation = await _cosmosService.GetItemAsync<CosmosConversation>(id.ToString(), conversation.UserId.ToString(), cancellationToken);
            if (cosmosConversation == null)
            {
                _logger.LogError("Conversation for id {Id} not found in Cosmos DB", id);
                throw new NotFoundException($"Conversation for id {id} not found");
            }

            var model = await _ctx.Models
                        .AsNoTracking()
                        .Where(x => x.Id == request.ModelId && !x.DateDeactivated.HasValue)
                        .Select(x => new { x.DeploymentName, x.ProviderId })
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
            conversation.Name = name;
            conversation.DateModified = date;
            await _ctx.SaveChangesAsync(cancellationToken);

            // Patch the two fields that changed. Passing the SQL entity to a full-document replace
            // here overwrote the Cosmos document with the relational shape and destroyed the
            // transcript — and this runs from inside a live stream, on the conversation's first turn.
            await _cosmosService.PatchItemAsync(conversation.Id.ToString(), conversation.UserId.ToString(),
            [
                PatchOperation.Set("/name", name),
                PatchOperation.Set("/dateModified", date),
            ], cancellationToken);
        }

        public async Task<PaginatedResponseDto<ConversationDto>> SearchConversationsAsync(string? name, int skip = 0, int take = 20, CancellationToken cancellationToken = default)
        {
            var userId = _tokenService.GetOid();

            var query = _ctx.Conversations
                .AsNoTracking()
                .Where(x => x.UserId == userId && !x.DateDeactivated.HasValue);

            if (!string.IsNullOrWhiteSpace(name))
            {
                query = query.Where(x => !string.IsNullOrWhiteSpace(x.Name) && EF.Functions.Like(x.Name, $"%{name}%"));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(x => x.DateCreated)
                .Skip(skip)
                .Take(take)
                .Select(s => s.MapToChatDto())
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

            await _cosmosService.PatchItemAsync(request.Id.ToString(), userId.ToString(),
            [
                PatchOperation.Set("/name", request.Name),
                PatchOperation.Set("/dateModified", date),
            ], cancellationToken);

            return await GetConversationAsync(request.Id, cancellationToken);
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
        public IAsyncEnumerable<string?> StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            _createChatStreamActionValidator.ValidateAndThrow(request);

            var userId = _tokenService.GetOid();

            return StreamConversationCoreAsync(id, request, userId, cancellationToken);
        }

        /// <summary>
        /// Runs a single chat turn under the conversation lock: loads history, streams the model
        /// response to the caller, then persists the turn.
        /// </summary>
        /// <param name="id">The unique identifier of the conversation.</param>
        /// <param name="request">The validated stream request.</param>
        /// <param name="userId">The object identifier of the calling user, resolved by the caller.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>An asynchronous sequence of response fragments as the model produces them.</returns>
        /// <exception cref="ConversationBusyException">Thrown when another turn is already in flight for this conversation.</exception>
        /// <exception cref="NotFoundException">Thrown when the conversation does not exist for this user.</exception>
        private async IAsyncEnumerable<string?> StreamConversationCoreAsync(Guid id, CreateConversationStreamActionDto request, Guid userId, [EnumeratorCancellation] CancellationToken cancellationToken)
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

            var cosmosConversation = await _cosmosService.GetItemAsync<CosmosConversation>(id.ToString(), userId.ToString(), cancellationToken);
            if (cosmosConversation == null)
            {
                _logger.LogError("Conversation {Id} not found.", id);
                throw new NotFoundException($"Conversation {id} not found.");
            }

            // Only the seeded system prompt so far, i.e. this is the conversation's first turn:
            // name it from the opening prompt before answering it.
            if (cosmosConversation.Messages.Count == 1)
            {
                await UpdateConversationNameAsync(id, request, cancellationToken);
            }

            // Conversations created before the transcript was persisted correctly were written
            // from the relational entity, which has no messages array at all. Every correctly
            // created conversation carries at least the system prompt, so an empty transcript here
            // means the property is absent rather than empty — and Cosmos rejects an append whose
            // parent path does not exist, so such a document has to be seeded instead.
            var transcriptExists = cosmosConversation.Messages.Count > 0;

            var conversations = new List<ChatMessage>(cosmosConversation.Messages.Select(x => new ChatMessage(MappingService.MapToChatRole(x.Role), x.Content)))
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
            long totalInputTokens = 0, totalOutputTokens = 0;

            // The lease set (null when no MCPs are selected) must stay alive for the whole
            // stream: the attached tools invoke through the leased clients. await using
            // releases the leases on completion, fault, or client disconnect alike.
            await using (toolLeases)
            {
                await foreach (var message in chatClient.GetStreamingResponseAsync(conversations, chatOptions, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!string.IsNullOrEmpty(message.Text))
                    {
                        sb.Append(message.Text);
                    }

                    if (message.Contents != null &&
                        message.Contents.Count > 0)
                    {
                        // Check for usage content to track token consumption during streaming
                        var usageContent = message.Contents.OfType<UsageContent>().FirstOrDefault();
                        if (usageContent != null)
                        {
                            totalInputTokens += usageContent.Details?.InputTokenCount ?? 0;
                            totalOutputTokens += usageContent.Details?.OutputTokenCount ?? 0;
                        }
                    }
                    yield return message.Text;
                }
            }

            var date = DateTimeOffset.UtcNow;

            await _ctx.Conversations
                .Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.InputTokens, x => x.InputTokens + totalInputTokens)
                    .SetProperty(x => x.OutputTokens, x => x.OutputTokens + totalOutputTokens)
                    .SetProperty(x => x.DateModified, date),
                    cancellationToken);

            // The deployment name, not the display label: the transcript is append-only, so the value
            // recorded here has to stay meaningful after a model is renamed in the catalog, and it is
            // the deployment that identifies which model actually served and billed the turn.
            var userMessage = new CosmosConversationMessage
            {
                Id = Guid.NewGuid(),
                Content = request.Prompt,
                DateCreated = date,
                Model = model.DeploymentName,
                Role = ChatRoles.User,
                Tokens = 0,
            };
            var assistantMessage = new CosmosConversationMessage
            {
                Id = Guid.NewGuid(),
                Content = sb.ToString(),
                DateCreated = date,
                Model = model.DeploymentName,
                Role = ChatRoles.Assistant,
                Usage = new CosmosConversationUsage
                {
                    InputTokens = totalInputTokens,
                    OutputTokens = totalOutputTokens
                }
            };

            // Appended server-side rather than re-read, mutated and replaced. A full-document
            // replace would silently revert anything written since the read at the top of this
            // method — a rename or a soft delete landing mid-stream — and would rewrite the entire
            // transcript on every turn, which grows the request toward the 2 MB item ceiling.
            // Operations apply in order, so seeding the array first leaves the second append valid.
            var persisted = await _cosmosService.PatchItemAsync(id.ToString(), userId.ToString(),
            [
                transcriptExists
                    ? PatchOperation.Add("/messages/-", userMessage)
                    : PatchOperation.Set<List<CosmosConversationMessage>>("/messages", [userMessage]),
                PatchOperation.Add("/messages/-", assistantMessage),
                PatchOperation.Increment("/messageCount", 2),
                PatchOperation.Increment("/totalTokens", totalInputTokens + totalOutputTokens),
                PatchOperation.Set("/dateModified", date),
            ], cancellationToken);

            if (!persisted)
            {
                // The token counters have already been committed to SQL, so failing silently here
                // would bill the turn and lose it. Nothing can be returned to the caller at this
                // point — the answer has been streamed — so this has to be caught in the logs.
                _logger.LogError("Conversation {Id} document is missing; the completed turn was not persisted.", id);
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

        /// <inheritdoc />
        public async Task<ChatConversationDto> GetConversationMessagesAsync(Guid id, CancellationToken cancellationToken)
        {
            var userId = _tokenService.GetOid();
            var cosmosConversation = await _cosmosService.GetItemAsync<CosmosConversation>(id.ToString(), userId.ToString(), cancellationToken);
            if (cosmosConversation == null)
            {
                _logger.LogError("Conversation {Id} not found.", id);
                throw new NotFoundException($"Conversation {id} not found.");
            }

            var messages = cosmosConversation.Messages
                            .Where(x => x.Role != ChatRoles.System)
                            .Select(x => new ConversationMessageDto()
                            {
                                Text = x.Content ?? string.Empty,
                                Role = x.Role
                            })
                            .ToList() ?? [];

            return new()
            {
                Id = id,
                Name = cosmosConversation.Name,
                DateCreated = cosmosConversation.DateCreated,
                DateModified = cosmosConversation.DateModified,
                Messages = messages
            };
        }

        /// <summary>
        /// Creates chat options for the stream and, when MCP servers are selected, resolves
        /// their tools through <see cref="IMcpToolProvider"/> (permission-checked, cached) and
        /// attaches them to the options.
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

            if (!string.IsNullOrWhiteSpace(projectInstructions))
            {
                // Carried as request-level instructions rather than injected into the message list:
                // that is the channel each provider maps to its own instruction slot, and it keeps
                // the messages equal to the transcript instead of transcript-plus-derived-context.
                chatOptions.Instructions = ConversationPrompts.BuildProjectInstructionsPrompt(projectInstructions);
            }

            var selectedServerIds = mcps.Select(m => m.Id).Distinct().ToArray();
            if (selectedServerIds.Length == 0)
            {
                return (chatOptions, null);
            }

            if (!model.IsToolEnabled)
            {
                throw new ValidationException(
                [
                    new ValidationFailure(nameof(CreateConversationStreamActionDto.McpServers),
                        "The selected model does not support tools; remove the MCP server selection or choose a tool-enabled model.")
                ]);
            }

            var toolLeases = await _mcpToolProvider.AcquireToolsAsync(selectedServerIds, cancellationToken).ConfigureAwait(false);
            if (toolLeases.Tools.Count > 0)
            {
                chatOptions.Tools = [.. toolLeases.Tools];
                chatOptions.AllowMultipleToolCalls = true;
            }

            return (chatOptions, toolLeases);
        }
    }
}
