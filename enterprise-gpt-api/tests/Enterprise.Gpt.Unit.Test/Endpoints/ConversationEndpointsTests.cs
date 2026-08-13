using Andes.Extensions.AI;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using System.Text.Json;
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

    private static AssistantUiEvent TextDelta(string text)
    {
        return new AssistantUiEvent { Kind = AssistantUiEventKind.TextDelta, Text = text };
    }

    private static async IAsyncEnumerable<AssistantUiEvent> Events(params AssistantUiEvent[] values)
    {
        await Task.Yield();

        foreach (var value in values)
        {
            yield return value;
        }
    }

    private static async IAsyncEnumerable<AssistantUiEvent> FaultedStream(Exception exception)
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

    /// <summary>
    /// Splits a written body into its SSE frames and strips the <c>data:</c> prefix, so a test can
    /// assert on payloads without restating the framing every time.
    /// </summary>
    private static List<string> ReadFrames(HttpContext context)
    {
        return
        [
            .. ReadBody(context)
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(frame => frame.StartsWith("data: ", StringComparison.Ordinal)
                    ? frame["data: ".Length..]
                    : throw new InvalidOperationException($"Frame is not a data line: '{frame}'"))
        ];
    }

    private static AssistantUiEvent Deserialize(string json)
    {
        return JsonSerializer.Deserialize(json, AssistantUiJsonContext.Default.AssistantUiEvent)
            ?? throw new InvalidOperationException($"Frame did not deserialize: '{json}'");
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
        _conversationService.GetConversationMessagesAsync(id, Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ConversationEndpoints.GetConversationMessagesAsync(
            id, _conversationService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    /// <summary>
    /// The paging arguments are optional on the wire, so a client that sends neither still gets
    /// the newest page.
    /// </summary>
    [Fact]
    public async Task GetConversationMessagesAsync_NoPagingArguments_RequestsTheNewestPage()
    {
        var id = Guid.NewGuid();
        _conversationService.GetConversationMessagesAsync(id, Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatConversationDto { Id = id });

        await ConversationEndpoints.GetConversationMessagesAsync(id, _conversationService, TestContext.Current.CancellationToken);

        await _conversationService.Received(1).GetConversationMessagesAsync(id, 100, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConversationMessagesAsync_PagingArguments_PassesThemThrough()
    {
        var id = Guid.NewGuid();
        _conversationService.GetConversationMessagesAsync(id, Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatConversationDto { Id = id });

        await ConversationEndpoints.GetConversationMessagesAsync(
            id, _conversationService, TestContext.Current.CancellationToken, take: 25, before: 400);

        await _conversationService.Received(1).GetConversationMessagesAsync(id, 25, 400, Arg.Any<CancellationToken>());
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
    public async Task StreamConversationAsync_ServiceYieldsEvents_WritesOneFrameEachInOrderUnderStreamingHeaders()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Events(TextDelta("Hel"), TextDelta("lo")));

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        var frames = ReadFrames(context);
        Assert.Equal(2, frames.Count);
        Assert.Equal("Hel", Deserialize(frames[0]).Text);
        Assert.Equal("lo", Deserialize(frames[1]).Text);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"].ToString());
    }

    // Activity events carry no text at all, so the frame has to stay well formed on the payload's
    // own discriminator rather than on there being something to render.
    [Fact]
    public async Task StreamConversationAsync_ServiceYieldsAToolActivity_WritesItAsItsOwnFrame()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Events(new AssistantUiEvent
            {
                Kind = AssistantUiEventKind.ActivityStarted,
                ScopeId = "scope-1",
                DisplayName = "Andes Test",
                ToolKind = ToolKind.McpTool,
                Depth = 1
            }));

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        var uiEvent = Deserialize(Assert.Single(ReadFrames(context)));
        Assert.Equal(AssistantUiEventKind.ActivityStarted, uiEvent.Kind);
        Assert.Equal("Andes Test", uiEvent.DisplayName);
        Assert.Equal(ToolKind.McpTool, uiEvent.ToolKind);
        Assert.Null(uiEvent.Text);
    }

    // The kind travels as a string rather than an ordinal so the contract stays readable and stable
    // for the TypeScript client the package ships alongside it.
    [Fact]
    public async Task StreamConversationAsync_ServiceYieldsAnEvent_WritesCamelCasePropertiesAndStringKinds()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Events(TextDelta("hi")));

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        var json = Assert.Single(ReadFrames(context));
        Assert.Contains("\"kind\":\"TextDelta\"", json);
        Assert.Contains("\"text\":\"hi\"", json);
    }

    // A turn that produced nothing still owes the client a well-formed empty stream rather than a
    // bare 200 the reader cannot interpret.
    [Fact]
    public async Task StreamConversationAsync_ServiceYieldsNothing_StillSetsStreamingHeaders()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Events());

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
