using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Rendering;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Enterprise.Gpt.Service.Tool;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Conversation = Enterprise.Gpt.Entity.Conversation;

namespace Enterprise.Gpt.Unit.Test.Services;

/// <summary>
/// The transcript read path: what a client sees when it reopens a conversation.
/// </summary>
public sealed class ConversationTranscriptReadTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IAzureCosmosService _cosmosService = Substitute.For<IAzureCosmosService>();
    private readonly ConversationService _service;

    public ConversationTranscriptReadTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);

        var services = new ServiceCollection()
            .AddSingleton(new TiktokenTokenizerCache(NullLogger<TiktokenTokenizerCache>.Instance))
            .AddSingleton<ITokenEstimator>(sp => new TiktokenTokenEstimator(
                Guid.Empty, sp.GetRequiredService<TiktokenTokenizerCache>(), Options.Create(new TokenEstimationOptions())))
            .BuildServiceProvider();
        var resolver = new TokenEstimatorResolver(services, NullLogger<TokenEstimatorResolver>.Instance);

        _service = new ConversationService(
            NullLogger<ConversationService>.Instance,
            Substitute.For<IModelService>(),
            Substitute.For<IChatClientResolver>(),
            Substitute.For<IMcpToolProvider>(),
            Substitute.For<IDocumentRetrievalService>(),
            Substitute.For<IConversationLockService>(),
            _tokenService,
            _cosmosService,
            resolver,
            new MarkdownRenderer(),
            new PromptEstimator(resolver, Options.Create(new TokenEstimationOptions())),
            new ContextBudgetCalculator(NullLogger<ContextBudgetCalculator>.Instance),
            Options.Create(new ContextBudgetOptions()),
            new ChatMetrics(new DummyMeterFactory()),
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

    private static CosmosConversationHeader BuildHeader(Guid conversationId, long messageCount = 5, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;

        return new CosmosConversationHeader
        {
            Id = conversationId,
            UserId = KnownIds.SeedUserId,
            ConversationId = conversationId,
            Name = "Planning",
            MessageCount = messageCount,
            DateCreated = date,
            DateModified = date,
            DateDeactivated = dateDeactivated
        };
    }

    private void SetUpHeader(Guid conversationId, CosmosConversationHeader? header)
    {
        _cosmosService.GetItemAsync<CosmosConversationHeader>(
                conversationId.ToString(), KnownIds.SeedUserId, conversationId, Arg.Any<CancellationToken>())
            .Returns(header);
    }

    /// <summary>
    /// The query orders newest-first so a page can be taken off the tail; the service reverses it
    /// for display.
    /// </summary>
    private void SetUpNewestFirstPage(Guid conversationId, string? continuationToken, params (long Sequence, ChatRoles Role, string Content)[] messages)
    {
        var page = messages
            .Select(message => new CosmosConversationMessage
            {
                Id = Guid.NewGuid(),
                UserId = KnownIds.SeedUserId,
                ConversationId = conversationId,
                Sequence = message.Sequence,
                Role = MappingService.MapToRoleName(message.Role),
                Content = message.Content,
                HtmlContent = $"<p>{message.Content}</p>",
                Tokens = 3,
                TokenAccuracy = TokenAccuracies.Estimated,
                DateCreated = DateTimeOffset.UtcNow
            })
            .ToList();

        _cosmosService.QueryPageAsync<CosmosConversationMessage>(
                Arg.Any<QueryDefinition>(), KnownIds.SeedUserId, conversationId, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new CosmosPage<CosmosConversationMessage>(page, continuationToken));
    }

    [Fact]
    public async Task GetConversationMessagesAsync_NewestFirstPage_ReturnsItOldestFirst()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId));
        SetUpNewestFirstPage(conversationId, null,
            (3, ChatRoles.Assistant, "Third"),
            (2, ChatRoles.User, "Second"),
            (1, ChatRoles.Assistant, "First"));

        var result = await _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], result.Messages.Select(message => message.Sequence));
        Assert.Equal(["First", "Second", "Third"], result.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task GetConversationMessagesAsync_WhenRead_CarriesTheStoredHtmlAndTokenFields()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId));
        SetUpNewestFirstPage(conversationId, null, (1, ChatRoles.User, "Hello"));

        var result = await _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken);

        var message = Assert.Single(result.Messages);
        Assert.Equal("<p>Hello</p>", message.HtmlContent);
        Assert.Equal(3, message.Tokens);
        Assert.Equal(TokenAccuracies.Estimated, message.TokenAccuracy);
        Assert.Equal(ChatRoles.User, message.Role);
    }

    /// <summary>
    /// The total counts every allocated position, including the system message the response never
    /// returns, so it is a progress indicator rather than the length of this list.
    /// </summary>
    [Fact]
    public async Task GetConversationMessagesAsync_WhenRead_ReportsTheHeadersTotalAndWhetherMoreRemain()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId, messageCount: 42));
        SetUpNewestFirstPage(conversationId, "next-page", (1, ChatRoles.User, "Hello"));

        var result = await _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken);

        Assert.Equal(42, result.TotalMessageCount);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_LastPage_ReportsNoMoreRemaining()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId));
        SetUpNewestFirstPage(conversationId, null, (1, ChatRoles.User, "Hello"));

        var result = await _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken);

        Assert.False(result.HasMore);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1_000, 100)]
    [InlineData(50, 50)]
    public async Task GetConversationMessagesAsync_TakeOutsideTheAllowedRange_IsClampedServerSide(int requested, int expected)
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId));
        SetUpNewestFirstPage(conversationId, null, (1, ChatRoles.User, "Hello"));

        await _service.GetConversationMessagesAsync(conversationId, requested, null, TestContext.Current.CancellationToken);

        await _cosmosService.Received(1).QueryPageAsync<CosmosConversationMessage>(
            Arg.Any<QueryDefinition>(), KnownIds.SeedUserId, conversationId, expected, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConversationMessagesAsync_BeforeCursor_BindsItAsAQueryParameter()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId));
        SetUpNewestFirstPage(conversationId, null, (1, ChatRoles.User, "Hello"));

        await _service.GetConversationMessagesAsync(conversationId, 100, 400, TestContext.Current.CancellationToken);

        await _cosmosService.Received(1).QueryPageAsync<CosmosConversationMessage>(
            Arg.Is<QueryDefinition>(query => query.QueryText.Contains("c.sequence < @before")),
            KnownIds.SeedUserId, conversationId, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The system message is the largest fixed message in a conversation and no client renders it,
    /// so it is excluded in the query rather than transferred and filtered.
    /// </summary>
    [Fact]
    public async Task GetConversationMessagesAsync_WhenRead_ExcludesSystemMessagesInTheQuery()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId));
        SetUpNewestFirstPage(conversationId, null, (1, ChatRoles.User, "Hello"));

        await _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken);

        await _cosmosService.Received(1).QueryPageAsync<CosmosConversationMessage>(
            Arg.Is<QueryDefinition>(query => query.QueryText.Contains("c.role != @systemRole")),
            KnownIds.SeedUserId, conversationId, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConversationMessagesAsync_HeaderExistsWithNoMessages_ReturnsAnEmptyList()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId, messageCount: 1));
        SetUpNewestFirstPage(conversationId, null);

        var result = await _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_UnknownConversation_ThrowsNotFound()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A soft-deleted conversation is gone as far as every other route is concerned, and the export
    /// path enforces the same rule — two read paths disagreeing about it would be worse than either.
    /// </summary>
    [Fact]
    public async Task GetConversationMessagesAsync_SoftDeletedConversation_ThrowsNotFound()
    {
        var conversationId = Guid.NewGuid();
        SetUpHeader(conversationId, BuildHeader(conversationId, dateDeactivated: DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetConversationMessagesAsync(conversationId, 100, null, TestContext.Current.CancellationToken));
    }
}
