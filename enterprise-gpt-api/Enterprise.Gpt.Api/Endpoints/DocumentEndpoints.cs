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
    /// Minimal API endpoints for conversation documents. Replaces the former <c>DocumentsController</c>.
    /// Uploading is gated by the <c>Upload File</c> permission, which every user holds by default; reading
    /// job status and the supported-format list is available to any authenticated caller.
    /// </summary>
    /// <remarks>
    /// Routes match the controller they replace exactly, so existing clients need no change.
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

            group.MapGet("upload-status/{jobId}", GetJobStatus)
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
            _ => "Processing",
        };
    }
}
