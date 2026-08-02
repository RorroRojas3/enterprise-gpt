using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using System.Runtime.CompilerServices;
using System.Text;
using Conversation = Enterprise.Gpt.Entity.Conversation;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
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

        bool IsConversationBusy(Guid id);
    }

    public class ConversationService(ILogger<ConversationService> logger,
        IModelService modelService,
        [FromKeyedServices("azureaifoundry")] IChatClient azureAIFoundry,
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
        private readonly IChatClient _azureAIFoundry = azureAIFoundry;
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

            var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);
            var date = DateTimeOffset.UtcNow;
            var newChat = new Conversation()
            {
                Name = "New Conversation",
                UserId = userId,
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
            await _ctx.SaveChangesAsync(cancellationToken);

            await _cosmosService.CreateItemAsync(newChat, userId.ToString(), cancellationToken);

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

            var cosmosConversation = await _cosmosService.GetItemAsync<CosmosConversation>(id.ToString(), userId.ToString(), cancellationToken);
            if (cosmosConversation != null)
            {
                cosmosConversation.DateDeactivated = date;
                await _cosmosService.UpdateItemAsync(cosmosConversation, cosmosConversation.Id.ToString(), userId.ToString(), cancellationToken);
            }

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

            // Deactivate all chunks for documents in these conversations
            var conversationIdsInClause = string.Join(", ", conversationIds.Select(id => $"'{id}'"));
            var cosmosQuery =
                $"SELECT * FROM c WHERE c.id IN ({conversationIdsInClause}) " +
                $"AND c.UserId = '{userId}' AND IS_NULL(c.DateDeactivated)";
            var chats = await _cosmosService.GetItemsAsync<CosmosConversation>(cosmosQuery);
            foreach (var chat in chats)
            {
                chat.DateDeactivated = date;
                await _cosmosService.UpdateItemAsync(chat, chat.Id.ToString(), userId.ToString(), cancellationToken);
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

            var modelName = await _ctx.Models
                        .AsNoTracking()
                        .Where(x => x.Id == request.ModelId && !x.DateDeactivated.HasValue)
                        .Select(x => x.Name)
                        .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                _logger.LogError("Model with id {Id} not found", request.ModelId);
                throw new InvalidOperationException($"Model with id {request.ModelId} not found");
            }

            var response = await _azureAIFoundry.GetResponseAsync([
                                 new ChatMessage(ChatRole.System, ConversationPrompts.BuildNamingPrompt()),
                                 new ChatMessage(ChatRole.User, $"Create a conversation name based on the following prompt, please make it 25 maximum and make it a string. Do not have the name on the conversation nor the id. Just the name based on the prompt. The result must be a string, not markdown. Prompt: {request.Prompt}")
                             ], new() { ModelId = modelName }, cancellationToken);
            if (response == null)
            {
                _logger.LogError("Failed to create name for chat id {Id}", id);
                throw new InvalidOperationException($"Failed to create name for id {id}");
            }

            var name = response.Messages.Last().Text?.Trim() ?? string.Empty;
            conversation.Name = name;
            conversation.DateModified = DateTimeOffset.UtcNow;
            await _ctx.SaveChangesAsync(cancellationToken);

            await _cosmosService.UpdateItemAsync(conversation, conversation.Id.ToString(), conversation.UserId.ToString(), cancellationToken);
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

            var date = DateTimeOffset.UtcNow;
            var rows = await _ctx.Conversations
                .Where(x => x.Id == request.Id && x.UserId == userId && !x.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Name, request.Name)
                    .SetProperty(x => x.DateModified, date),
                    cancellationToken);

            var cosmosConversation = await _cosmosService.GetItemAsync<CosmosConversation>(request.Id.ToString(), userId.ToString(), cancellationToken);
            if (cosmosConversation != null)
            {
                cosmosConversation.Name = request.Name;
                cosmosConversation.DateModified = date;
                await _cosmosService.UpdateItemAsync(cosmosConversation, cosmosConversation.Id.ToString(), userId.ToString(), cancellationToken);
            }

            return await GetConversationAsync(request.Id, cancellationToken);
        }

        /// <inheritdoc />
        public bool IsConversationBusy(Guid id) => _conversationLockService.IsConversationBusy(id);

        /// <inheritdoc />
        public async IAsyncEnumerable<string?> StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            _createChatStreamActionValidator.ValidateAndThrow(request);

            var lockReleaser = await _conversationLockService.TryAcquireLockAsync(id, cancellationToken);
            if (lockReleaser == null)
            {
                _logger.LogWarning("Conversation {ConversationId} is already being processed.", id);
                throw new InvalidOperationException($"Conversation {id} is currently being processed. Please wait for the current request to complete.");
            }

            var userId = _tokenService.GetOid();
            using (lockReleaser)
            {
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

                if (cosmosConversation.Messages.Count == 1)
                {
                    await UpdateConversationNameAsync(id, request, cancellationToken);
                }

                var conversations = new List<ChatMessage>(cosmosConversation.Messages.Select(x => new ChatMessage(MappingService.MapToChatRole(x.Role), x.Content)))
                {
                    new(ChatRole.User, request.Prompt)
                };

                var model = await _modelService.GetModelAsync(request.ModelId, cancellationToken);
                var chatClient = _azureAIFoundry;
                var (chatOptions, toolLeases) = await CreateChatOptionsAsync(id, model, request.McpServers, cancellationToken).ConfigureAwait(false);
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

                cosmosConversation = await _cosmosService.GetItemAsync<CosmosConversation>(id.ToString(), userId.ToString(), cancellationToken);
                if (cosmosConversation != null)
                {
                    cosmosConversation.DateModified = date;
                    cosmosConversation.TotalTokens = cosmosConversation.TotalTokens + totalInputTokens + totalOutputTokens;
                    cosmosConversation.Messages.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        Content = request.Prompt,
                        DateCreated = date,
                        Model = model.Name,
                        Role = ChatRoles.User,
                        Tokens = 0,
                    });
                    cosmosConversation.Messages.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        Content = sb.ToString(),
                        DateCreated = date,
                        Model = model.Name,
                        Role = ChatRoles.Assistant,
                        Usage = new CosmosConversationUsage
                        {
                            InputTokens = totalInputTokens,
                            OutputTokens = totalOutputTokens
                        }
                    });
                }


                await _cosmosService.UpdateItemAsync(cosmosConversation, id.ToString(), userId.ToString(), cancellationToken);
            }
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
            Guid sessionId, ModelDto model, List<McpServerSelectionDto> mcps, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(model);

            ChatOptions chatOptions = new()
            {
                ModelId = model.Name,
                ConversationId = sessionId.ToString()
            };

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
