using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;

namespace Enterprise.Gpt.Api.Endpoints
{
    /// <summary>
    /// Minimal API endpoints for documents: upload and download, into and out of either a conversation
    /// or a project, plus the caller-scoped listing behind the documents library. Uploading is gated by
    /// the <c>Upload File</c> permission, which every user holds by default; downloading is gated on
    /// owning the parent, listing is scoped to the caller's own documents, and reading job status and
    /// the supported-format list is available to any authenticated caller.
    /// </summary>
    /// <remarks>
    /// The conversation routes match the <c>DocumentsController</c> they replaced exactly, so existing
    /// clients needed no change. Project uploads were added to this group rather than to
    /// <see cref="ProjectEndpoints"/> so both owner types share one size limit, one permission filter and
    /// the single <c>upload-status</c> route.
    /// </remarks>
    public static class DocumentEndpoints
    {
        /// <summary>
        /// Maps the <c>api/documents</c> endpoint group.
        /// </summary>
        /// <param name="app">The route builder to map the group onto.</param>
        /// <returns>The same <paramref name="app"/> for chaining.</returns>
        public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("api/documents")
                .RequireAuthorization()
                .WithTags("Documents")
                // Every route in the group is authorized, so the challenge applies uniformly.
                .ProducesProblem(StatusCodes.Status401Unauthorized);

            var maxFileSizeBytes = app.ServiceProvider.GetRequiredService<IOptions<DocumentOptions>>().Value.MaxFileSizeBytes;

            group.MapPost("conversations/{conversationId:guid}", CreateConversationDocumentAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile))
                // Minimal APIs opt IFormFile endpoints into antiforgery validation, which throws at
                // request time unless the middleware is registered or the endpoint opts out. This API is
                // authenticated by bearer token and never by cookie, so a forged cross-site request cannot
                // carry the caller's credentials and CSRF does not apply.
                .DisableAntiforgery()
                .AddEndpointFilter(MaxUploadSizeEndpointFilter.Require(maxFileSizeBytes))
                // Not ProducesValidationProblem: this route's 400 is also raised by
                // MaxUploadSizeEndpointFilter, which carries no errors dictionary. OpenAPI allows one
                // schema per status, and ProblemDetails is the one true of both.
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPost("projects/{projectId:guid}", CreateProjectDocumentAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile))
                // Same reasoning as the conversation route above: bearer-token auth, so CSRF does not
                // apply and antiforgery would only fail the request at form-binding time.
                .DisableAntiforgery()
                .AddEndpointFilter(MaxUploadSizeEndpointFilter.Require(maxFileSizeBytes))
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);

            // A bare GET on the group root, so it collides with none of the multi-segment routes below.
            // Not gated on Upload File, like the downloads: that permission is about adding documents,
            // and this is a read scoped to the caller. ProducesValidationProblem() because ?type= is
            // rejected by the service with an errors dictionary naming the parameter; a binding
            // failure on ?skip= or ?take= still returns a 400 without one, and OpenAPI allows a single
            // schema per status, so the richer of the two is declared.
            group.MapGet("", GetUserDocumentsAsync)
                .ProducesValidationProblem();

            // Three segments, so neither route collides with upload-status/{jobId} or file-extensions.
            // Not gated on Upload File: that permission is about adding documents, and owning the
            // parent conversation or project is what decides whether one can be read back.
            group.MapGet("conversations/{conversationId:guid}/{documentId:guid}", GetConversationDocumentDownloadAsync)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

            group.MapGet("projects/{projectId:guid}/{documentId:guid}", GetProjectDocumentDownloadAsync)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

            // Not gated on Upload File, for the same reason the downloads are not: that permission is
            // about adding documents, and owning the parent is what decides whether one can be removed.
            // Gating it would strand a user's own documents the moment the grant were revoked.
            group.MapDelete("conversations/{conversationId:guid}/{documentId:guid}", DeactivateConversationDocumentAsync)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("upload-status/{jobId}", GetJobStatus)
                .ProducesProblem(StatusCodes.Status404NotFound);

            // Two segments, so it cannot collide with the three-segment document delete above. Not gated
            // on Upload File, for the reason the status read beside it is not: owning the job is what
            // decides whether it may be stopped.
            group.MapDelete("upload-status/{jobId}", CancelUploadAsync)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("file-extensions", GetFileExtensions);

            return app;
        }

        // Validation and ownership failures throw ValidationException / NotFoundException in the service
        // and surface as 400 and 404 through the exception-handler chain.
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

        // Kept alongside the conversation upload rather than under api/projects so both share this
        // group's size limit, permission filter, and the single upload-status route below.
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

        // Query parameters carry defaults so the paging arguments stay optional — an absent ?skip=
        // would otherwise fail binding with a 400 — which forces them behind the injected service,
        // as C# requires optional parameters last.
        internal static async Task<Ok<PaginatedResponseDto<UserDocumentDto>>> GetUserDocumentsAsync(
            IDocumentService documentService, int skip = 0, int take = 20, string? type = null,
            string? name = null, CancellationToken cancellationToken = default)
        {
            var response = await documentService.GetUserDocumentsAsync(skip, take, type, name, cancellationToken);

            return TypedResults.Ok(response);
        }

        // The response carries a signed URL rather than the file itself, so storage serves the bytes and
        // this process allocates nothing per download. Ownership failures throw NotFoundException in the
        // service and surface as 404, which is also what a document belonging to another user returns.
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

        // The body carries a signed URL that reads the file with no credentials of its own, so a private
        // browser cache would otherwise write it to disk where it outlives the link's few minutes.
        private static void PreventCaching(HttpResponse httpResponse)
        {
            httpResponse.Headers.CacheControl = "no-store";
        }

        // Idempotent: cancelling a job that already finished removes what it produced, so one call always
        // leaves nothing behind. The orchestration lives in the service because it spans the status store,
        // the cancellation registry and a soft-delete cascade.
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

            // Scoped to the caller who queued it. The snapshot carries the created document id and the
            // failure detail, so another user reading it would learn both. 404 rather than 403 keeps a
            // job id from being confirmed as valid, matching how conversations are handled. The message
            // is deliberately generic so the not-yours case and the never-existed case read alike.
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

        private static async Task<byte[]> ReadFileAsync(IFormFile file, CancellationToken cancellationToken)
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream, cancellationToken);
            return memoryStream.ToArray();
        }

        // Mirrors the Hangfire job-state vocabulary the frontend already understands. Stages added after
        // that contract was set map onto Processing so an older client still sees a sensible progression.
        private static string MapState(JobStatus status) => status switch
        {
            JobStatus.Queued => "Enqueued",
            JobStatus.Processed => "Succeeded",
            JobStatus.Failed => "Failed",
            // Explicit rather than left to the default below: falling through to Processing would leave a
            // client polling a job that will never move again.
            JobStatus.Cancelled => "Failed",
            _ => "Processing",
        };
    }
}
