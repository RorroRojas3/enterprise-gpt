using Andes.Extensions.AI;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Export;
using System.Text;
using System.Text.Json;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for conversations: the chat turns themselves plus the transcript and the
/// list the UI's sidebar is built from. Replaces the former <c>ConversationsController</c>, the last
/// MVC controller in the API.
/// </summary>
/// <remarks>
/// Every route is scoped to the signed-in user rather than gated on a permission, exactly as
/// <see cref="ProjectEndpoints"/> is — a conversation is owned, and there is no administrative view
/// of another user's chats. Uploading into a conversation lives on <see cref="DocumentEndpoints"/>.
/// </remarks>
public static class ConversationEndpoints
{
    /// <summary>
    /// Maps the <c>api/conversations</c> endpoint group.
    /// </summary>
    /// <param name="app">The route builder to map the group onto.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/conversations")
            .RequireAuthorization()
            .WithTags("Conversations")
            // Every route in the group is authorized, so the challenge applies uniformly.
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // 404 rather than an empty page when ?projectId= names a project the caller does not own.
        // The 400 is a binding failure on one of the four typed query parameters, so it carries no
        // errors dictionary — ProducesProblem(400), not ProducesValidationProblem(), for the reason
        // spelled out on the favorite route below.
        group.MapGet("search", SearchConversationsAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("{id:guid}", GetConversationAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        // The 400 is a binding failure on ?take= or ?before=, so it carries no errors dictionary —
        // ProducesProblem(400) rather than ProducesValidationProblem(), as on the routes below.
        group.MapGet("{id:guid}/messages", GetConversationMessagesAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("{id:guid}/documents", GetConversationDocumentsAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        // Content-negotiated by query parameter rather than by Accept header, so a plain browser
        // link can request either format. The 400 carries an errors dictionary, because an
        // unsupported ?format= is rejected by a validator rather than by binding.
        group.MapGet("{id:guid}/export", ExportConversationAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/html")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", CreateConversationAsync)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("", UpdateConversationAsync)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        // ProducesProblem(400) rather than ProducesValidationProblem(): the request has no
        // FluentValidation validator, so its only 400 is a binding failure and carries no errors
        // dictionary. OpenAPI allows one schema per status, so the two cannot both be declared.
        group.MapPut("{id:guid}/favorite", SetConversationFavoriteAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("bulk", DeactivateConversationsBulkAsync)
            .ProducesValidationProblem();
        // No 404 declared on purpose: deactivating a conversation that is missing or already
        // deactivated logs a warning and returns, so this route only ever answers 204.
        group.MapDelete("{id:guid}", DeactivateConversationAsync);

        // The stream carries no typed body, so the success response has to be declared by hand.
        // Its error surface is the widest in the API because a turn resolves a model, acquires the
        // conversation lock and connects to whatever MCP servers the caller selected.
        group.MapPost("{id:guid}/stream", StreamConversationAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    // Query parameters follow the service here rather than leading, as elsewhere in the API:
    // they need defaults so the paging arguments stay optional, and C# requires optional
    // parameters last. Without defaults an absent ?skip= would fail binding with a 400.
    internal static async Task<Ok<PaginatedResponseDto<ConversationDto>>> SearchConversationsAsync(
        IConversationService conversationService, string? name = null, int skip = 0, int take = 20,
        bool? isFavorite = null, Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        var response = await conversationService.SearchConversationsAsync(name, skip, take, isFavorite, projectId, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Not-found paths throw NotFoundException in the service and surface as 404 through the
    // exception-handler chain. A conversation belonging to another user is reported the same way, so
    // this API cannot be used to probe for conversation ids.
    internal static async Task<Ok<ConversationDetailDto>> GetConversationAsync(
        Guid id, IConversationService conversationService, CancellationToken cancellationToken)
    {
        var response = await conversationService.GetConversationAsync(id, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Returns a file rather than a typed body, so it writes the response directly instead of going
    // through TypedResults: the payload is a whole HTML document or a JSON rendering of the stored
    // documents, neither of which is a DTO this API otherwise serializes.
    internal static async Task<IResult> ExportConversationAsync(
        Guid id, IConversationExportService exportService, string? format = null,
        CancellationToken cancellationToken = default)
    {
        var export = await exportService.ExportConversationAsync(id, format, cancellationToken);

        return Results.File(
            Encoding.UTF8.GetBytes(export.Content), export.ContentType, export.FileName);
    }

    // Paging is opt-in: with neither parameter the whole transcript is returned, as it always was.
    // Optional parameters must come last, as on the search route above, or an absent ?take= fails
    // binding with a 400.
    internal static async Task<Ok<ChatConversationDto>> GetConversationMessagesAsync(
        Guid id, IConversationService conversationService, int? take = null,
        DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        var response = await conversationService.GetConversationMessagesAsync(id, take, before, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Not-found paths throw NotFoundException in the service and surface as 404 through the
    // exception-handler chain. A conversation belonging to another user is reported the same way, so
    // this route cannot be used to probe for conversation ids.
    internal static async Task<Ok<List<ConversationDocumentDto>>> GetConversationDocumentsAsync(
        Guid id, IConversationService conversationService, CancellationToken cancellationToken)
    {
        var response = await conversationService.GetConversationDocumentsAsync(id, cancellationToken);
        return TypedResults.Ok(response);
    }

    internal static async Task<Created<ConversationDto>> CreateConversationAsync(
        CreateConversationActionDto request, IConversationService conversationService, CancellationToken cancellationToken)
    {
        var response = await conversationService.CreateConversationAsync(request, cancellationToken);
        return TypedResults.Created($"/api/conversations/{response.Id}", response);
    }

    // The id travels in the body rather than the route, unlike the sibling modules: this is a
    // full-representation PUT whose DTO already carries the id, and the shape predates the move off
    // MVC. Changing it would break the UI's rename call for no gain.
    internal static async Task<Ok<ConversationDto>> UpdateConversationAsync(
        UpdateConversationActionDto request, IConversationService conversationService, CancellationToken cancellationToken)
    {
        var response = await conversationService.UpdateConversationAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Its own route rather than a field on the PUT above: that PUT is a full representation, so a
    // rename that forgot to echo the flag back would silently un-favourite the conversation.
    internal static async Task<NoContent> SetConversationFavoriteAsync(
        Guid id, SetConversationFavoriteActionDto request, IConversationService conversationService,
        CancellationToken cancellationToken)
    {
        await conversationService.SetConversationFavoriteAsync(id, request, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<NoContent> DeactivateConversationAsync(
        Guid id, IConversationService conversationService, CancellationToken cancellationToken)
    {
        await conversationService.DeactivateConversationAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    // [FromBody] is required, not decorative: minimal APIs do not infer a body parameter for DELETE
    // (nor GET, HEAD or OPTIONS), and without the attribute mapping this route throws at startup.
    // The verb stays DELETE because the UI already sends a body with it.
    internal static async Task<NoContent> DeactivateConversationsBulkAsync(
        [FromBody] DeactivateConversationsBulkActionDto request, IConversationService conversationService,
        CancellationToken cancellationToken)
    {
        await conversationService.DeactivateConversationsBulkAsync(request, cancellationToken);
        return TypedResults.NoContent();
    }

    // Headers are set on the first frame rather than up front: setting them eagerly left an error
    // response (a 409 for a conversation that is already streaming, say) carrying streaming headers
    // alongside its JSON body. Connection is omitted deliberately — it is a hop-by-hop header Kestrel
    // owns, and it is invalid under HTTP/2.
    //
    // TypedResults.ServerSentEvents is the obvious .NET 10 answer and still is not usable here: it
    // commits the response before enumerating, and everything that can fail a turn — the conversation
    // lock, the transcript read, resolving the model, leasing MCP servers — happens on the first move
    // of the sequence. Under it, a 409 or a 404 would arrive after a 200 had already gone out.
    //
    // Each event is written as its own SSE frame with no event name, so a client reads them off a
    // single default channel and switches on the payload's own "kind" discriminator. The payload is
    // the tracking middleware's UI contract, which ships a matching TypeScript definition and fold
    // function; the source-generated context is what keeps its JSON identical to that definition.
    internal static async Task StreamConversationAsync(
        Guid id, CreateConversationStreamActionDto request, IConversationService conversationService,
        HttpResponse response, CancellationToken cancellationToken)
    {
        var headersSet = false;

        await foreach (var uiEvent in conversationService.StreamConversationAsync(id, request, cancellationToken))
        {
            if (!headersSet)
            {
                SetStreamingHeaders(response);
                headersSet = true;
            }

            // Serialized JSON never contains a raw newline, so one data line per frame is always
            // well formed and no continuation handling is needed.
            var json = JsonSerializer.Serialize(uiEvent, AssistantUiJsonContext.Default.AssistantUiEvent);

            await response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        // A turn that produced nothing still owes the client a well-formed empty stream
        // rather than a bare 200 the reader cannot interpret.
        if (!headersSet)
        {
            SetStreamingHeaders(response);
        }
    }

    /// <summary>
    /// Applies the response headers for a server-sent event stream.
    /// </summary>
    private static void SetStreamingHeaders(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Reverse proxies buffer a response by default, which holds every frame back until the turn
        // ends and defeats the point of streaming activity as it happens.
        response.Headers["X-Accel-Buffering"] = "no";
    }
}
