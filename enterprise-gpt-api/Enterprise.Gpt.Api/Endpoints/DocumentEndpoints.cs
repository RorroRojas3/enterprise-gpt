using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Documents;
using Enterprise.Gpt.Service.Documents.Extraction;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>
/// Document routes: uploads to and downloads from a conversation or project, and the caller's library.
/// </summary>
/// <remarks>
/// Project uploads live here rather than on <see cref="ProjectEndpoints"/> so both owner types share one size
/// limit, one permission filter and the single <c>upload-status</c> route.
/// </remarks>
public static class DocumentEndpoints
{
    #region Private methods

    // The body carries a signed URL that reads the file with no credentials of its own, so a private
    // browser cache would otherwise write it to disk where it outlives the link's few minutes.
    private static void PreventCaching(HttpResponse httpResponse)
    {
        httpResponse.Headers.CacheControl = "no-store";
    }

    private static async Task<byte[]> ReadFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
    }

    // The Hangfire job-state vocabulary the frontend matches; newer stages map onto Processing so an older
    // client still sees a sensible progression.
    private static string MapState(JobStatuses status) => status switch
    {
        JobStatuses.Queued => "Enqueued",
        JobStatuses.Processed => "Succeeded",
        JobStatuses.Failed => "Failed",
        // Explicit rather than left to the default below: falling through to Processing would leave a
        // client polling a job that will never move again.
        JobStatuses.Cancelled => "Failed",
        _ => "Processing",
    };

    #endregion

    #region Public static methods

    /// <summary>Maps the <c>api/documents</c> endpoint group.</summary>
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/documents")
            .RequireAuthorization()
            .WithTags("Documents")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        var maxFileSizeBytes = app.ServiceProvider.GetRequiredService<IOptions<DocumentOptions>>().Value.MaxFileSizeBytes;

        group.MapPost("conversations/{conversationId:guid}", CreateConversationDocumentAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile))
            // Bearer-token auth, never cookies, so CSRF does not apply; IFormFile endpoints would otherwise
            // demand antiforgery and throw at request time.
            .DisableAntiforgery()
            .AddEndpointFilter(MaxUploadSizeEndpointFilter.Require(maxFileSizeBytes))
            // Not ProducesValidationProblem: MaxUploadSizeEndpointFilter also raises a 400, with no errors
            // dictionary.
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("projects/{projectId:guid}", CreateProjectDocumentAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile))
            .DisableAntiforgery()
            .AddEndpointFilter(MaxUploadSizeEndpointFilter.Require(maxFileSizeBytes))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Not gated on Upload File: that permission is about adding documents, and this read is scoped to the
        // caller. A bad ?type= is a validation problem, so the richer 400 schema is declared.
        group.MapGet("", GetUserDocumentsAsync)
            .ProducesValidationProblem();

        // Three segments, so neither collides with upload-status/{jobId} or file-extensions. Owning the parent,
        // not Upload File, decides whether a document can be read back.
        group.MapGet("conversations/{conversationId:guid}/{documentId:guid}", GetConversationDocumentDownloadAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("projects/{projectId:guid}/{documentId:guid}", GetProjectDocumentDownloadAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Not gated on Upload File: that would strand a user's own documents the moment the grant were revoked.
        group.MapDelete("conversations/{conversationId:guid}/{documentId:guid}", DeactivateConversationDocumentAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("upload-status/{jobId}", GetJobStatus)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Two segments, so it cannot collide with the document delete above. Owning the job, not Upload File,
        // decides whether it may be stopped.
        group.MapDelete("upload-status/{jobId}", CancelUploadAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("file-extensions", GetFileExtensions);

        return app;
    }

    internal static async Task<Accepted<JobDto>> CreateConversationDocumentAsync(
        Guid conversationId, IFormFile file, IDocumentService documentService, CancellationToken cancellationToken)
    {
        // Buffered into memory here, on the request thread, because the background scope outlives the
        // request and its disposed form stream.
        var fileDto = new FileDto
        {
            FileName = file.FileName,
            ContentType = file.ContentType,
            Length = file.Length,
            Content = await ReadFileAsync(file, cancellationToken)
        };

        var response = await documentService.QueueConversationDocumentAsync(conversationId, fileDto, cancellationToken);

        return TypedResults.Accepted($"/api/documents/upload-status/{response.Id}", response);
    }

    internal static async Task<Accepted<JobDto>> CreateProjectDocumentAsync(
        Guid projectId, IFormFile file, IDocumentService documentService, CancellationToken cancellationToken)
    {
        var fileDto = new FileDto
        {
            FileName = file.FileName,
            ContentType = file.ContentType,
            Length = file.Length,
            Content = await ReadFileAsync(file, cancellationToken)
        };

        var response = await documentService.QueueProjectDocumentAsync(projectId, fileDto, cancellationToken);

        return TypedResults.Accepted($"/api/documents/upload-status/{response.Id}", response);
    }

    // Query parameters need defaults, or an absent ?skip= fails binding with a 400.
    internal static async Task<Ok<PaginatedResponseDto<UserDocumentDto>>> GetUserDocumentsAsync(
        IDocumentService documentService, int skip = 0, int take = 20, string? type = null,
        string? name = null, CancellationToken cancellationToken = default)
    {
        var response = await documentService.GetUserDocumentsAsync(skip, take, type, name, cancellationToken);

        return TypedResults.Ok(response);
    }

    // A signed URL rather than the file, so storage serves the bytes and this process allocates nothing per
    // download. Another user's document is a 404.
    internal static async Task<Ok<DocumentDownloadDto>> GetConversationDocumentDownloadAsync(
        Guid conversationId, Guid documentId, IDocumentService documentService, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        var response = await documentService.GetConversationDocumentDownloadAsync(conversationId, documentId, cancellationToken);

        PreventCaching(httpResponse);

        return TypedResults.Ok(response);
    }

    internal static async Task<Ok<DocumentDownloadDto>> GetProjectDocumentDownloadAsync(
        Guid projectId, Guid documentId, IDocumentService documentService, HttpResponse httpResponse, CancellationToken cancellationToken)
    {
        var response = await documentService.GetProjectDocumentDownloadAsync(projectId, documentId, cancellationToken);

        PreventCaching(httpResponse);

        return TypedResults.Ok(response);
    }

    internal static async Task<NoContent> DeactivateConversationDocumentAsync(
        Guid conversationId, Guid documentId, IDocumentService documentService, CancellationToken cancellationToken)
    {
        await documentService.DeactivateConversationDocumentAsync(conversationId, documentId, cancellationToken);

        return TypedResults.NoContent();
    }

    // Idempotent: cancelling a job that already finished removes what it produced.
    internal static async Task<NoContent> CancelUploadAsync(
        string jobId, IDocumentService documentService, CancellationToken cancellationToken)
    {
        await documentService.CancelUploadAsync(jobId, cancellationToken);

        return TypedResults.NoContent();
    }

    internal static Ok<JobStatusDto> GetJobStatus(
        string jobId, IJobStatusStore jobStatusStore, ITokenService tokenService)
    {
        var snapshot = jobStatusStore.Get(jobId);

        // The snapshot carries the document id and failure detail, so only its owner reads it. A generic 404,
        // not a 403, so a job id cannot be confirmed as valid.
        if (snapshot is null || snapshot.UserId != tokenService.GetOid())
        {
            throw new NotFoundException("Upload job not found.");
        }

        return TypedResults.Ok(new JobStatusDto
        {
            Id = jobId,
            State = MapState(snapshot.Status),
            Status = snapshot.Status.ToString(),
            Progress = snapshot.Progress,
            Message = snapshot.Message,
            CompletedUnits = snapshot.CompletedUnits,
            TotalUnits = snapshot.TotalUnits,
            DocumentId = snapshot.DocumentId,
            ErrorMessage = snapshot.ErrorMessage,
            UpdatedAt = snapshot.LastUpdated
        });
    }

    // Derived from the registered extractors rather than a list of its own, so the advertised formats
    // and the readable formats cannot drift apart.
    internal static Ok<IReadOnlyList<string>> GetFileExtensions(IDocumentTextExtractorFactory extractorFactory)
    {
        return TypedResults.Ok(extractorFactory.SupportedExtensionNames);
    }

    #endregion
}
