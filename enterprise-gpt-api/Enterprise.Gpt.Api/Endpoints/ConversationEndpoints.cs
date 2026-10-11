using System.Text.Json;
using Andes.Extensions.AI;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto.Actions.Conversations;
using Enterprise.Gpt.Dto.Conversations;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Service.Conversations;
using Enterprise.Gpt.Service.Exports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>Conversation routes: chat turns, transcripts and the sidebar list.</summary>
/// <remarks>
/// Scoped to the signed-in user rather than gated on a permission: there is no administrative view of another
/// user's chats.
/// </remarks>
public static class ConversationEndpoints
{
    #region Private methods

    private static void SetStreamingHeaders(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Reverse proxies buffer a response by default, which holds every frame back until the turn
        // ends and defeats the point of streaming activity as it happens.
        response.Headers["X-Accel-Buffering"] = "no";
    }

    #endregion

    #region Public static methods

    /// <summary>Maps the <c>api/conversations</c> endpoint group.</summary>
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/conversations")
            .RequireAuthorization()
            .WithTags("Conversations")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // 404 rather than an empty page when ?projectId= names a project the caller does not own. A bad ?sort=
        // or ?dir= is a validation problem and a binding failure a bare 400; OpenAPI allows one schema per
        // status, so the richer is declared.
        group.MapGet("search", SearchConversationsAsync)
            .ProducesValidationProblem()
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
        // Format by query parameter rather than Accept header, so a plain browser link can request any. The 503
        // is a format this deployment cannot render, with its own problem type to tell it from a transient one.
        group.MapGet("{id:guid}/export", ExportConversationAsync)
            .Produces(
                StatusCodes.Status200OK,
                contentType: "text/html",
                additionalContentTypes:
                [
                    "text/markdown",
                    "application/json",
                    "application/pdf",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                ])
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("", CreateConversationAsync)
            .AddEndpointFilter<ValidationEndpointFilter<CreateConversationActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("", UpdateConversationAsync)
            .AddEndpointFilter<ValidationEndpointFilter<UpdateConversationActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        // ProducesProblem(400): there is no validator, so the only 400 is a binding failure with no errors.
        group.MapPut("{id:guid}/favorite", SetConversationFavoriteAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        // The 400 is a validation problem naming `messageId` when the target is not an assistant message, or a
        // bare binding failure for a rating the enum does not define.
        group.MapPost("{id:guid}/messages/{messageId:guid}/feedback", SetMessageFeedbackAsync)
            .AddEndpointFilter<ValidationEndpointFilter<SetMessageFeedbackActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("bulk", DeactivateConversationsBulkAsync)
            .AddEndpointFilter<ValidationEndpointFilter<DeactivateConversationsBulkActionDto>>()
            .ProducesValidationProblem();
        // No 404 declared on purpose: deactivating a conversation that is missing or already
        // deactivated logs a warning and returns, so this route only ever answers 204.
        group.MapDelete("{id:guid}", DeactivateConversationAsync);

        // The widest error surface in the API: a turn resolves a model, takes the conversation lock and
        // connects to the caller's MCP servers.
        group.MapPost("{id:guid}/stream", StreamConversationAsync)
            .AddEndpointFilter<ValidationEndpointFilter<CreateConversationStreamActionDto>>()
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            // A selected server may need an API key the caller supplies and has not.
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    // Query parameters follow the service, unlike elsewhere in the API: they need defaults, or an absent
    // ?skip= fails binding with a 400.
    internal static async Task<Ok<PaginatedResponseDto<ConversationDto>>> SearchConversationsAsync(
        IConversationService conversationService, string? name = null, int skip = 0, int take = 20,
        bool? isFavorite = null, Guid? projectId = null, string? sort = null, string? dir = null,
        CancellationToken cancellationToken = default)
    {
        var response = await conversationService.SearchConversationsAsync(name, skip, take, isFavorite, projectId, sort, dir, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Another user's conversation is a 404 too, so ids cannot be probed.
    internal static async Task<Ok<ConversationDetailDto>> GetConversationAsync(
        Guid id, IConversationService conversationService, CancellationToken cancellationToken)
    {
        var response = await conversationService.GetConversationAsync(id, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Results.File writes an RFC 5987 filename* beside the plain one, so a conversation named in any script
    // downloads under its own name.
    internal static async Task<IResult> ExportConversationAsync(
        Guid id, IConversationExportService exportService, HttpResponse httpResponse,
        string? format = null, CancellationToken cancellationToken = default)
    {
        var export = await exportService.ExportConversationAsync(id, format, cancellationToken);

        // A transcript is the most sensitive thing this API serves, and the response is a file a
        // shared browser would otherwise keep on disk long after the reader signed out.
        httpResponse.Headers.CacheControl = "no-store";

        return Results.File(export.Content, export.ContentType, export.FileName);
    }

    // Paging is opt-in: with neither parameter the whole transcript is returned.
    internal static async Task<Ok<ChatConversationDto>> GetConversationMessagesAsync(
        Guid id, IConversationService conversationService, int? take = null,
        DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        var response = await conversationService.GetConversationMessagesAsync(id, take, before, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Another user's conversation is a 404 too, so ids cannot be probed.
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

    // The id travels in the body, unlike the sibling modules: moving it to the route would break the UI's
    // rename call for no gain.
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

    // 204 rather than the stored rating: the client knows what it asked for, and a reload reads the transcript.
    internal static async Task<NoContent> SetMessageFeedbackAsync(
        Guid id, Guid messageId, SetMessageFeedbackActionDto request,
        IConversationService conversationService, CancellationToken cancellationToken)
    {
        await conversationService.SetMessageFeedbackAsync(id, messageId, request, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<NoContent> DeactivateConversationAsync(
        Guid id, IConversationService conversationService, CancellationToken cancellationToken)
    {
        await conversationService.DeactivateConversationAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    // [FromBody] is required: minimal APIs infer no body for DELETE, and mapping would throw at startup.
    // The verb stays DELETE because the UI already sends a body with it.
    internal static async Task<NoContent> DeactivateConversationsBulkAsync(
        [FromBody] DeactivateConversationsBulkActionDto request, IConversationService conversationService,
        CancellationToken cancellationToken)
    {
        await conversationService.DeactivateConversationsBulkAsync(request, cancellationToken);
        return TypedResults.NoContent();
    }

    // Headers go out with the first frame, so an earlier failure (a 409, say) carries no streaming headers.
    // Not TypedResults.ServerSentEvents: it commits a 200 before the first move, where the lock, the model and
    // the MCP leases can still fail. Connection is omitted: it is hop-by-hop and invalid under HTTP/2.
    // Frames carry no event name; clients switch on the payload's "kind", and the source-generated context
    // keeps that JSON identical to the middleware's TypeScript contract.
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

    #endregion
}
