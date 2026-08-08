using Microsoft.AspNetCore.Http;
using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class ConversationEndpointsTests
{
    private readonly IConversationService _conversationService = Substitute.For<IConversationService>();

    private static ConversationDto CreateDto(string name = "Planning")
    {
        return new ConversationDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            DateCreated = DateTimeOffset.UtcNow.AddDays(-1),
            DateModified = DateTimeOffset.UtcNow
        };
    }

    private static CreateConversationStreamActionDto CreateStreamRequest()
    {
        return new CreateConversationStreamActionDto { Prompt = "Hello", ModelId = Guid.NewGuid() };
    }

    private static async IAsyncEnumerable<string?> Fragments(params string?[] values)
    {
        await Task.Yield();

        foreach (var value in values)
        {
            yield return value;
        }
    }

    private static async IAsyncEnumerable<string?> FaultedStream(Exception exception)
    {
        await Task.Yield();

        // The guard is always taken; it exists so the compiler keeps the yield below reachable,
        // which an async iterator has to contain to be one.
        if (exception is not null)
        {
            throw exception;
        }

        yield break;
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task SearchConversationsAsync_ServiceReturnsAPage_ReturnsOkWithPayload()
    {
        var expected = new PaginatedResponseDto<ConversationDto>
        {
            Items = [CreateDto()],
            TotalCount = 1,
            PageSize = 20,
            CurrentPage = 1
        };
        _conversationService.SearchConversationsAsync("plan", 0, 20, null, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, "plan", 0, 20, null, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task SearchConversationsAsync_NoQueryString_UsesTheDefaultPageAndNoFavoriteFilter()
    {
        _conversationService.SearchConversationsAsync(null, 0, 20, null, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<ConversationDto>());

        await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1).SearchConversationsAsync(null, 0, 20, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchConversationsAsync_FavoritesRequested_PassesTheFilterThrough()
    {
        _conversationService.SearchConversationsAsync(null, 0, 20, true, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<ConversationDto>());

        await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1).SearchConversationsAsync(null, 0, 20, true, Arg.Any<CancellationToken>());
    }

    // Not-found paths throw NotFoundException in the service and surface as 404 through the
    // exception-handler chain, so the handlers themselves only have the success shape to assert.
    [Fact]
    public async Task GetConversationAsync_ServiceReturnsConversation_ReturnsOkWithPayload()
    {
        var expected = new ConversationDetailDto { Id = Guid.NewGuid(), Name = "Planning" };
        _conversationService.GetConversationAsync(expected.Id, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ConversationEndpoints.GetConversationAsync(
            expected.Id, _conversationService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_ServiceReturnsTranscript_ReturnsOkWithPayload()
    {
        var id = Guid.NewGuid();
        var expected = new ChatConversationDto { Id = id, Name = "Planning" };
        _conversationService.GetConversationMessagesAsync(id, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ConversationEndpoints.GetConversationMessagesAsync(
            id, _conversationService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task CreateConversationAsync_ServiceCreatesConversation_ReturnsCreatedWithLocation()
    {
        var request = new CreateConversationActionDto();
        var created = CreateDto();
        _conversationService.CreateConversationAsync(request, Arg.Any<CancellationToken>()).Returns(created);

        var result = await ConversationEndpoints.CreateConversationAsync(
            request, _conversationService, TestContext.Current.CancellationToken);

        Assert.Same(created, result.Value);
        Assert.Equal($"/api/conversations/{created.Id}", result.Location);
    }

    [Fact]
    public async Task UpdateConversationAsync_ServiceUpdatesConversation_ReturnsOkWithPayload()
    {
        var request = new UpdateConversationActionDto { Id = Guid.NewGuid(), Name = "Renamed" };
        var updated = CreateDto("Renamed");
        _conversationService.UpdateConversationAsync(request, Arg.Any<CancellationToken>()).Returns(updated);

        var result = await ConversationEndpoints.UpdateConversationAsync(
            request, _conversationService, TestContext.Current.CancellationToken);

        Assert.Same(updated, result.Value);
    }

    [Fact]
    public async Task SetConversationFavoriteAsync_ServiceSucceeds_ReturnsNoContent()
    {
        var id = Guid.NewGuid();
        var request = new SetConversationFavoriteActionDto { IsFavorite = true };

        var result = await ConversationEndpoints.SetConversationFavoriteAsync(
            id, request, _conversationService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _conversationService.Received(1).SetConversationFavoriteAsync(id, request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateConversationAsync_ServiceSucceeds_ReturnsNoContent()
    {
        var id = Guid.NewGuid();

        var result = await ConversationEndpoints.DeactivateConversationAsync(
            id, _conversationService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _conversationService.Received(1).DeactivateConversationAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateConversationsBulkAsync_ServiceSucceeds_ReturnsNoContent()
    {
        var request = new DeactivateConversationsBulkActionDto { ConversationIds = [Guid.NewGuid(), Guid.NewGuid()] };

        var result = await ConversationEndpoints.DeactivateConversationsBulkAsync(
            request, _conversationService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _conversationService.Received(1).DeactivateConversationsBulkAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamConversationAsync_ServiceYieldsFragments_WritesThemInOrderUnderStreamingHeaders()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Fragments("Hel", "lo"));

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        Assert.Equal("Hello", ReadBody(context));
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task StreamConversationAsync_ServiceYieldsANullFragment_WritesItAsEmptyRatherThanFailing()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Fragments("a", null, "b"));

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        Assert.Equal("ab", ReadBody(context));
    }

    // A model that produced nothing still owes the client a well-formed empty stream rather than a
    // bare 200 the reader cannot interpret.
    [Fact]
    public async Task StreamConversationAsync_ServiceYieldsNothing_StillSetsStreamingHeaders()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Fragments());

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        Assert.Empty(ReadBody(context));
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
    }

    // The invariant the deferred-header design exists for: a turn that is rejected before its first
    // fragment must leave the response clean so the exception handlers can write a JSON problem body
    // rather than a 409 dressed as an event stream.
    [Fact]
    public async Task StreamConversationAsync_StreamFaultsBeforeTheFirstFragment_LeavesTheResponseUntouched()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(FaultedStream(new ConversationBusyException("Conversation is busy.")));

        await Assert.ThrowsAsync<ConversationBusyException>(() => ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken));

        Assert.Null(context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
        Assert.Empty(ReadBody(context));
    }
}
