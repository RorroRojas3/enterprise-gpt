using FluentValidation;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Runtime.CompilerServices;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Conversation = Enterprise.Gpt.Entity.Conversation;

namespace Enterprise.Gpt.Unit.Test.Services;

/// <summary>
/// Covers the MCP chat-wiring surface of <see cref="ConversationService.StreamConversationAsync"/>:
/// how selected MCP servers turn into tools on the <see cref="ChatOptions"/> handed to the
/// chat client, and how the tool leases are released when the stream ends.
/// </summary>
public sealed class ConversationServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IModelService _modelService = Substitute.For<IModelService>();
    private readonly IMcpToolProvider _mcpToolProvider = Substitute.For<IMcpToolProvider>();
    private readonly IConversationLockService _lockService = Substitute.For<IConversationLockService>();
    private readonly IAzureCosmosService _cosmosService = Substitute.For<IAzureCosmosService>();
    private readonly FakeChatClient _chatClient = new();
    private readonly ConversationService _service;

    public ConversationServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _lockService.TryAcquireLockAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDisposable>());
        _service = new ConversationService(
            NullLogger<ConversationService>.Instance,
            _modelService,
            _chatClient,
            _mcpToolProvider,
            _lockService,
            _tokenService,
            _cosmosService,
            new CreateConversationActionDtoValidator(),
            new CreateConversationStreamActionDtoValidator(),
            new DeactivateConversationsBulkActionDtoValidator(),
            new UpdateConversationActionDtoValidator(),
            _fixture.Context);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<Conversation> AddConversationAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation;
    }

    /// <summary>
    /// Seeds the Cosmos-side conversation with two messages so the conversation-naming path
    /// (taken only when a single system message exists) is skipped.
    /// </summary>
    private void SetUpCosmosConversation(Guid conversationId)
    {
        var date = DateTimeOffset.UtcNow;
        var cosmosConversation = new CosmosConversation
        {
            Id = conversationId,
            UserId = KnownIds.SeedUserId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date,
            Messages =
            [
                new() { Id = Guid.NewGuid(), Role = ChatRoles.System, Content = "System prompt.", DateCreated = date },
                new() { Id = Guid.NewGuid(), Role = ChatRoles.User, Content = "Earlier prompt.", DateCreated = date }
            ]
        };
        _cosmosService.GetItemAsync<CosmosConversation>(
                conversationId.ToString(), KnownIds.SeedUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(cosmosConversation);
    }

    private ModelDto SetUpModel(bool isToolEnabled)
    {
        var model = new ModelDto
        {
            Id = Guid.NewGuid(),
            ProviderId = KnownIds.SeedProviderId,
            Name = "gpt-test",
            DisplayName = "GPT Test",
            Description = "A test model.",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = isToolEnabled
        };
        _modelService.GetModelAsync(model.Id, Arg.Any<CancellationToken>()).Returns(model);

        return model;
    }

    private async Task<List<string?>> StreamToEndAsync(Guid conversationId, CreateConversationStreamActionDto request)
    {
        var updates = new List<string?>();
        await foreach (var text in _service.StreamConversationAsync(conversationId, request, TestContext.Current.CancellationToken))
        {
            updates.Add(text);
        }

        return updates;
    }

    /// <summary>
    /// The Cosmos document must be created from the transcript projection, not the relational
    /// entity: writing the entity left the conversation with no messages array and no system
    /// prompt, which the model never saw and the first-turn naming path could never detect.
    /// </summary>
    [Fact]
    public async Task CreateConversationAsync_WhenCalled_PersistsTranscriptWithSystemPrompt()
    {
        await _service.CreateConversationAsync(new CreateConversationActionDto(), TestContext.Current.CancellationToken);

        await _cosmosService.Received(1).CreateItemAsync(
            Arg.Is<CosmosConversation>(cosmos =>
                cosmos!.Messages.Count == 1
                && cosmos.Messages[0].Role == ChatRoles.System
                && cosmos.Messages[0].Content.Length > 0),
            KnownIds.SeedUserId.ToString(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Renaming used to hand the relational entity to a full-document replace, overwriting the
    /// transcript with the relational shape — and it runs from inside a live stream.
    /// </summary>
    [Fact]
    public async Task UpdateConversationNameAsync_WhenNamed_PatchesNameWithoutReplacingDocument()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        _chatClient.NamingResponse = "Named Conversation";
        // The seeded model id, not a substituted one: this path resolves the model name straight
        // from the database rather than through IModelService.
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = KnownIds.SeedModelId,
            McpServers = []
        };

        await _service.UpdateConversationNameAsync(conversation.Id, request, TestContext.Current.CancellationToken);

        await _cosmosService.Received(1).PatchItemAsync(
            conversation.Id.ToString(),
            KnownIds.SeedUserId.ToString(),
            Arg.Is<IReadOnlyList<PatchOperation>>(operations =>
                operations!.Count == 2
                && operations[0].OperationType == PatchOperationType.Set && operations[0].Path == "/name"
                && operations[1].OperationType == PatchOperationType.Set && operations[1].Path == "/dateModified"),
            Arg.Any<CancellationToken>());
        await _cosmosService.DidNotReceiveWithAnyArgs().UpdateItemAsync(
            default(CosmosConversation)!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StreamConversationAsync_WhenConversationLockUnavailable_ThrowsConversationBusyException()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        var model = SetUpModel(isToolEnabled: true);
        _lockService.TryAcquireLockAsync(conversation.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDisposable?>((IDisposable?)null));
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers = []
        };

        await Assert.ThrowsAsync<ConversationBusyException>(
            () => StreamToEndAsync(conversation.Id, request));

        Assert.Null(_chatClient.CapturedOptions);
    }

    /// <summary>
    /// Contention surfaces on the first <c>MoveNextAsync</c> — before any fragment is produced —
    /// so the controller can still answer with a 409 rather than a half-shaped stream.
    /// </summary>
    [Fact]
    public async Task StreamConversationAsync_WhenConversationLockUnavailable_ThrowsOnFirstMoveNext()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        var model = SetUpModel(isToolEnabled: true);
        _lockService.TryAcquireLockAsync(conversation.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDisposable?>((IDisposable?)null));
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers = []
        };

        var enumerator = _service.StreamConversationAsync(conversation.Id, request, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ConversationBusyException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task StreamConversationAsync_WhenStreamCompletes_ReleasesConversationLock()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        var model = SetUpModel(isToolEnabled: true);
        var releaser = Substitute.For<IDisposable>();
        _lockService.TryAcquireLockAsync(conversation.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDisposable?>(releaser));
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers = []
        };

        await StreamToEndAsync(conversation.Id, request);

        releaser.Received(1).Dispose();
    }

    /// <summary>
    /// The turn is appended with a patch rather than a read-modify-replace, so a write that lands
    /// between loading the history and persisting the answer is not reverted.
    /// </summary>
    [Fact]
    public async Task StreamConversationAsync_WhenStreamCompletes_AppendsMessagesWithoutReplacingDocument()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        var model = SetUpModel(isToolEnabled: true);
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers = []
        };

        await StreamToEndAsync(conversation.Id, request);

        await _cosmosService.Received(1).PatchItemAsync(
            conversation.Id.ToString(),
            KnownIds.SeedUserId.ToString(),
            Arg.Is<IReadOnlyList<PatchOperation>>(operations =>
                operations!.Count == 5
                && operations[0].OperationType == PatchOperationType.Add && operations[0].Path == "/messages/-"
                && operations[1].OperationType == PatchOperationType.Add && operations[1].Path == "/messages/-"
                && operations[2].OperationType == PatchOperationType.Increment && operations[2].Path == "/messageCount"
                && operations[3].OperationType == PatchOperationType.Increment && operations[3].Path == "/totalTokens"
                && operations[4].OperationType == PatchOperationType.Set && operations[4].Path == "/dateModified"),
            Arg.Any<CancellationToken>());
        await _cosmosService.DidNotReceiveWithAnyArgs().UpdateItemAsync(
            default(CosmosConversation)!, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Conversations written before the transcript was persisted correctly have no messages array,
    /// and Cosmos rejects an append whose parent path is absent, so the first turn on such a
    /// document has to seed the array instead.
    /// </summary>
    [Fact]
    public async Task StreamConversationAsync_WhenTranscriptArrayAbsent_SeedsMessagesInsteadOfAppending()
    {
        var conversation = await AddConversationAsync();
        var date = DateTimeOffset.UtcNow;
        _cosmosService.GetItemAsync<CosmosConversation>(
                conversation.Id.ToString(), KnownIds.SeedUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new CosmosConversation
            {
                Id = conversation.Id,
                UserId = KnownIds.SeedUserId,
                Name = "Legacy Conversation",
                DateCreated = date,
                DateModified = date,
                Messages = []
            });
        var model = SetUpModel(isToolEnabled: true);
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers = []
        };

        await StreamToEndAsync(conversation.Id, request);

        await _cosmosService.Received(1).PatchItemAsync(
            conversation.Id.ToString(),
            KnownIds.SeedUserId.ToString(),
            Arg.Is<IReadOnlyList<PatchOperation>>(operations =>
                operations!.Count == 5
                && operations[0].OperationType == PatchOperationType.Set && operations[0].Path == "/messages"
                && operations[1].OperationType == PatchOperationType.Add && operations[1].Path == "/messages/-"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamConversationAsync_McpServersSelectedWithNonToolModel_ThrowsValidationExceptionWithoutAcquiringTools()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        var model = SetUpModel(isToolEnabled: false);
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers = [new McpServerSelectionDto { Id = Guid.NewGuid() }]
        };

        await Assert.ThrowsAsync<ValidationException>(
            () => StreamToEndAsync(conversation.Id, request));

        await _mcpToolProvider.DidNotReceiveWithAnyArgs()
            .AcquireToolsAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StreamConversationAsync_NoMcpServersSelected_StreamsWithoutToolsOrProvider()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        var model = SetUpModel(isToolEnabled: true);
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers = []
        };

        var updates = await StreamToEndAsync(conversation.Id, request);

        Assert.Equal(["Hello", " world"], updates);
        var capturedOptions = _chatClient.CapturedOptions;
        Assert.NotNull(capturedOptions);
        Assert.Null(capturedOptions.Tools);
        Assert.Equal(model.Name, capturedOptions.ModelId);
        Assert.Equal(conversation.Id.ToString(), capturedOptions.ConversationId);
        await _mcpToolProvider.DidNotReceiveWithAnyArgs()
            .AcquireToolsAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StreamConversationAsync_McpServersSelectedWithToolModel_AttachesLeasedToolsAndDisposesLeaseSet()
    {
        var conversation = await AddConversationAsync();
        SetUpCosmosConversation(conversation.Id);
        var model = SetUpModel(isToolEnabled: true);
        var firstServerId = Guid.NewGuid();
        var secondServerId = Guid.NewGuid();
        var tools = new List<AITool> { new FakeTool(), new FakeTool() };
        var leaseSet = Substitute.For<IMcpToolLeaseSet>();
        leaseSet.Tools.Returns(tools);
        _mcpToolProvider.AcquireToolsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(leaseSet);
        var request = new CreateConversationStreamActionDto
        {
            Prompt = "Hello",
            ModelId = model.Id,
            McpServers =
            [
                new McpServerSelectionDto { Id = firstServerId },
                new McpServerSelectionDto { Id = secondServerId },
                new McpServerSelectionDto { Id = firstServerId }
            ]
        };

        var updates = await StreamToEndAsync(conversation.Id, request);

        Assert.Equal(["Hello", " world"], updates);
        await _mcpToolProvider.Received(1).AcquireToolsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids!.Count == 2
                && ids.Contains(firstServerId)
                && ids.Contains(secondServerId)),
            Arg.Any<CancellationToken>());
        var capturedOptions = _chatClient.CapturedOptions;
        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions.Tools);
        Assert.Equal(tools, capturedOptions.Tools);
        Assert.True(capturedOptions.AllowMultipleToolCalls);
        await leaseSet.Received(1).DisposeAsync();
    }

    /// <summary>
    /// Captures the <see cref="ChatOptions"/> the service builds and yields a short, fixed
    /// stream. <see cref="GetResponseAsync"/> throws so any unexpected trip through the
    /// conversation-naming path fails the test loudly.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        public ChatOptions? CapturedOptions { get; private set; }

        /// <summary>
        /// Gets or sets the name the conversation-naming path should return. Left <see langword="null"/>,
        /// that path throws, so tests that do not expect naming to run still fail loudly.
        /// </summary>
        public string? NamingResponse { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (NamingResponse is null)
            {
                throw new NotSupportedException("The naming path should not run in these tests.");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, NamingResponse)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, " world");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeTool : AITool
    {
    }
}
