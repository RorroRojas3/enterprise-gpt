using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Export;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class ConversationEndpointsTests
{
    private readonly IConversationService _conversationService = Substitute.For<IConversationService>();
    private readonly IConversationExportService _exportService = Substitute.For<IConversationExportService>();

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
        // Named arguments throughout: the call takes four optional filters in a row, three of them
        // commonly null, and a fifth added later would otherwise shift a value into the wrong slot
        // with nothing failing to compile.
        _conversationService.SearchConversationsAsync(
            name: "plan", skip: 0, take: 20, isFavorite: null, projectId: null,
            cancellationToken: Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, name: "plan", skip: 0, take: 20, isFavorite: null, projectId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task SearchConversationsAsync_NoQueryString_UsesTheDefaultPageAndNoFilters()
    {
        _conversationService.SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: null, projectId: null,
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<ConversationDto>());

        await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1).SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: null, projectId: null,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchConversationsAsync_OrderRequested_PassesBothParametersThrough()
    {
        _conversationService.SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: null, projectId: null, sort: "name", dir: "asc",
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<ConversationDto>());

        await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, sort: "name", dir: "asc",
            cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1).SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: null, projectId: null, sort: "name", dir: "asc",
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchConversationsAsync_FavoritesRequested_PassesTheFilterThrough()
    {
        _conversationService.SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: true, projectId: null,
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<ConversationDto>());

        await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1).SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: true, projectId: null,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchConversationsAsync_ProjectRequested_PassesTheFilterThrough()
    {
        var projectId = Guid.NewGuid();
        _conversationService.SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: null, projectId: projectId,
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<ConversationDto>());

        await ConversationEndpoints.SearchConversationsAsync(
            _conversationService, projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1).SearchConversationsAsync(
            name: null, skip: 0, take: 20, isFavorite: null, projectId: projectId,
            cancellationToken: Arg.Any<CancellationToken>());
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
    public async Task GetConversationDocumentsAsync_ServiceReturnsDocuments_ReturnsOkWithPayload()
    {
        var conversationId = Guid.NewGuid();
        List<ConversationDocumentDto> expected = [new() { Id = Guid.NewGuid(), ConversationId = conversationId, Name = "spec.pdf", Extension = ".pdf", MimeType = "application/pdf", Size = 10 }];
        _conversationService.GetConversationDocumentsAsync(conversationId, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ConversationEndpoints.GetConversationDocumentsAsync(
            conversationId, _conversationService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_ServiceReturnsTranscript_ReturnsOkWithPayload()
    {
        var id = Guid.NewGuid();
        var expected = new ChatConversationDto { Id = id, Name = "Planning" };
        _conversationService
            .GetConversationMessagesAsync(id, Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await ConversationEndpoints.GetConversationMessagesAsync(
            id, _conversationService, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    /// <summary>
    /// Paging is opt-in. A request with neither parameter must reach the service with both unset, so
    /// the client that reads this endpoint today keeps receiving the whole transcript.
    /// </summary>
    [Fact]
    public async Task GetConversationMessagesAsync_NoQueryParameters_AsksForTheWholeTranscript()
    {
        var id = Guid.NewGuid();
        _conversationService
            .GetConversationMessagesAsync(id, Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatConversationDto { Id = id });

        await ConversationEndpoints.GetConversationMessagesAsync(
            id, _conversationService, cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1)
            .GetConversationMessagesAsync(id, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConversationMessagesAsync_PagingParameters_AreForwardedToTheService()
    {
        var id = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;
        _conversationService
            .GetConversationMessagesAsync(id, Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatConversationDto { Id = id });

        await ConversationEndpoints.GetConversationMessagesAsync(
            id, _conversationService, take: 25, before: before,
            cancellationToken: TestContext.Current.CancellationToken);

        await _conversationService.Received(1)
            .GetConversationMessagesAsync(id, 25, before, Arg.Any<CancellationToken>());
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
    public async Task SetMessageFeedbackAsync_ServiceSucceeds_ReturnsNoContent()
    {
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var request = new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up };

        var result = await ConversationEndpoints.SetMessageFeedbackAsync(
            id, messageId, request, _conversationService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _conversationService.Received(1)
            .SetMessageFeedbackAsync(id, messageId, request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMessageFeedbackAsync_NullRating_ForwardsTheWithdrawalRatherThanSkippingTheCall()
    {
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var request = new SetMessageFeedbackActionDto { Rating = null };

        await ConversationEndpoints.SetMessageFeedbackAsync(
            id, messageId, request, _conversationService, TestContext.Current.CancellationToken);

        await _conversationService.Received(1).SetMessageFeedbackAsync(
            id, messageId, Arg.Is<SetMessageFeedbackActionDto>(x => x.Rating == null), Arg.Any<CancellationToken>());
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

    // The slot US-1101 puts the assistant message id in. Worth pinning at the wire rather than only
    // at the service: the payload is serialized through a source-generated context that writes
    // exactly the properties it knows, so a property the generator has not been told about is
    // silently dropped rather than failing to compile.
    [Fact]
    public async Task StreamConversationAsync_FinishedCarriesMetadata_WritesItAsAJsonObject()
    {
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Events(new AssistantUiEvent
            {
                Kind = AssistantUiEventKind.Finished,
                Metadata = new Dictionary<string, string>
                {
                    [IConversationService.AssistantMessageIdMetadataKey] = messageId.ToString()
                }
            }));

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        var json = Assert.Single(ReadFrames(context));
        Assert.Contains($"\"metadata\":{{\"assistantMessageId\":\"{messageId}\"}}", json);

        var uiEvent = Deserialize(json);
        Assert.Equal(
            messageId.ToString(),
            Assert.Contains(
                IConversationService.AssistantMessageIdMetadataKey,
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(uiEvent.Metadata)));
    }

    // A frame that sets nothing serializes exactly as it did before the slot existed, which is what
    // makes the upgrade invisible to a client that ignores it.
    [Fact]
    public async Task StreamConversationAsync_EventCarriesNoMetadata_OmitsTheKeyEntirely()
    {
        var id = Guid.NewGuid();
        var request = CreateStreamRequest();
        var context = CreateContext();
        _conversationService.StreamConversationAsync(id, request, Arg.Any<CancellationToken>())
            .Returns(Events(new AssistantUiEvent { Kind = AssistantUiEventKind.Finished }));

        await ConversationEndpoints.StreamConversationAsync(
            id, request, _conversationService, context.Response, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("metadata", Assert.Single(ReadFrames(context)));
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

    #region Export
    /// <summary>
    /// The response is a rendered transcript, which is the most sensitive thing this API serves as a
    /// file. Without this header a shared browser keeps it on disk long after the reader signs out.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_Always_SetsCacheControlNoStore()
    {
        var id = Guid.NewGuid();
        _exportService.ExportConversationAsync(id, "md", Arg.Any<CancellationToken>())
            .Returns(new ConversationExport("# Notes"u8.ToArray(), "text/markdown; charset=utf-8", "Notes.md"));
        var context = CreateContext();

        await ConversationEndpoints.ExportConversationAsync(
            id, _exportService, context.Response, "md", TestContext.Current.CancellationToken);

        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    /// <summary>
    /// Results.File writes the attachment disposition; this pins that the renderer's own file name is
    /// what reaches it, since that name is what the browser saves and the client never supplies one.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_Always_ReturnsAFileNamedByTheRenderer()
    {
        var id = Guid.NewGuid();
        var content = "%PDF-1.7"u8.ToArray();
        _exportService.ExportConversationAsync(id, "pdf", Arg.Any<CancellationToken>())
            .Returns(new ConversationExport(content, "application/pdf", "Planning-notes.pdf"));

        var result = await ConversationEndpoints.ExportConversationAsync(
            id, _exportService, CreateContext().Response, "pdf", TestContext.Current.CancellationToken);

        var file = Assert.IsAssignableFrom<IFileHttpResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("Planning-notes.pdf", file.FileDownloadName);
    }

    /// <summary>
    /// The header is set after the service call, so a failure leaves the problem response to the
    /// exception handler chain rather than stamping a file header on it.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_ServiceThrows_SetsNoHeaders()
    {
        var id = Guid.NewGuid();
        _exportService.ExportConversationAsync(id, "pdf", Arg.Any<CancellationToken>())
            .Returns<ConversationExport>(_ => throw new ExportRendererNotConfiguredException(ConversationExportFormats.Pdf));
        var context = CreateContext();

        await Assert.ThrowsAsync<ExportRendererNotConfiguredException>(
            () => ConversationEndpoints.ExportConversationAsync(
                id, _exportService, context.Response, "pdf", TestContext.Current.CancellationToken));

        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task ExportConversationAsync_NoFormat_PassesNullThrough()
    {
        var id = Guid.NewGuid();
        _exportService.ExportConversationAsync(id, null, Arg.Any<CancellationToken>())
            .Returns(new ConversationExport("<html></html>"u8.ToArray(), "text/html; charset=utf-8", "Notes.html"));

        await ConversationEndpoints.ExportConversationAsync(
            id, _exportService, CreateContext().Response, cancellationToken: TestContext.Current.CancellationToken);

        await _exportService.Received(1).ExportConversationAsync(id, null, Arg.Any<CancellationToken>());
    }
    #endregion
}
