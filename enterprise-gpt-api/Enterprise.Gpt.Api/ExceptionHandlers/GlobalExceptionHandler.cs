using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Api.ExceptionHandlers
{
    /// <summary>
    /// Fallback exception handler that maps the remaining exception types
    /// (<see cref="ArgumentException"/>, <see cref="ArgumentNullException"/>,
    /// <see cref="ConversationBusyException"/>,
    /// <see cref="InvalidOperationException"/>, <see cref="NotFoundException"/>,
    /// <see cref="KeyNotFoundException"/>, <see cref="ForbiddenException"/>,
    /// <see cref="McpAuthorizationRequiredException"/>,
    /// <see cref="McpServerUnavailableException"/>) and any
    /// otherwise unhandled exception to an RFC 9457 <see cref="ProblemDetails"/> response.
    /// Always returns <see langword="true"/> so any exception receives a consistent payload.
    /// </summary>
    /// <param name="problemDetailsService">Writes the content-negotiated problem details response.</param>
    /// <param name="logger">Logger instance for recording error information.</param>
    internal sealed class GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService = problemDetailsService;
        private readonly ILogger<GlobalExceptionHandler> _logger = logger;

        /// <summary>
        /// Handles the specified exception by logging it and writing a <see cref="ProblemDetails"/>
        /// response to the HTTP response stream.
        /// </summary>
        /// <param name="httpContext">The HTTP context for the current request.</param>
        /// <param name="exception">The exception to handle.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns><see langword="true"/> always; this handler is the final fallback.</returns>
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            // The trace id is omitted from the template: the hosting layer already puts it on every
            // log scope as TraceId, and repeating it here under the same name with a different value
            // is what made response and log ids disagree.
            _logger.LogError(exception, "An error occurred: {Message}", exception.Message);

            // Belt and braces. ExceptionHandlerMiddleware already rethrows without invoking any
            // handler once the response has started, so this is not what keeps a faulting SSE stream
            // intact — the middleware is. Kept because the status and headers really are immutable
            // here, and assigning either would throw and mask the original exception.
            if (httpContext.Response.HasStarted)
            {
                return true;
            }

            var problemDetails = MapToProblemDetails(exception);

            // The writer reads Response.StatusCode rather than ProblemDetails.Status, and the
            // exception handler middleware has already set it to 500 before this handler runs.
            httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

            if (!await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problemDetails,
                Exception = exception
            }))
            {
                // The writer declines media types outside JSON, writing nothing. A caller sending
                // Accept: text/html still gets a body rather than a bare status — and Enrich has to
                // run explicitly here, because this path never reaches CustomizeProblemDetails.
                ProblemDetailsRegistration.Enrich(httpContext, problemDetails);
                await httpContext.Response.WriteAsJsonAsync(
                    problemDetails,
                    options: null,
                    contentType: ProblemDetailsRegistration.ProblemMediaType,
                    cancellationToken);
            }

            return true;
        }

        private static ProblemDetails MapToProblemDetails(Exception exception) =>
            exception switch
            {
                ArgumentNullException => Create(StatusCodes.Status400BadRequest, exception.Message),
                ArgumentException => Create(StatusCodes.Status400BadRequest, exception.Message),
                // Kestrel throws this when the request body exceeds the limit
                // MaxUploadSizeEndpointFilter lowers for chunked uploads, which declare no
                // Content-Length for its first layer to check. Its StatusCode is already the right
                // client error (413 there), so falling through to the 500 arm would report a server
                // fault for a client mistake.
                BadHttpRequestException badRequest => Create(badRequest.StatusCode, badRequest.Message),
                // Contention is a temporary state the caller can retry out of, so it must not
                // collapse into the 400 bucket alongside validation failures.
                ConversationBusyException => Create(
                    StatusCodes.Status409Conflict, exception.Message, ProblemTypes.ConversationBusy),
                InvalidOperationException => Create(StatusCodes.Status400BadRequest, exception.Message),
                NotFoundException => Create(
                    StatusCodes.Status404NotFound, exception.Message, ProblemTypes.NotFound),
                KeyNotFoundException => Create(
                    StatusCodes.Status404NotFound, exception.Message, ProblemTypes.NotFound),
                ForbiddenException => Create(
                    StatusCodes.Status403Forbidden, exception.Message, ProblemTypes.Forbidden),
                // 403, not 401: a 401 would send clients into token-refresh loops that cannot
                // fix a consent or Conditional Access requirement. The distinct type URI is what
                // lets a client tell this apart from an ordinary denial and prompt for consent.
                McpAuthorizationRequiredException authorizationRequired => WithServerName(
                    Create(StatusCodes.Status403Forbidden, exception.Message, ProblemTypes.McpAuthorizationRequired),
                    authorizationRequired.ServerName),
                McpServerUnavailableException serverUnavailable => WithServerName(
                    Create(StatusCodes.Status502BadGateway, exception.Message, ProblemTypes.McpServerUnavailable),
                    serverUnavailable.ServerName),
                // The real message is suppressed: an unmapped exception may carry connection
                // strings, file paths, or query text.
                _ => Create(StatusCodes.Status500InternalServerError, "An unexpected internal server error occurred.")
            };

        // A null problemType leaves Type and Title unset, which is what makes ProblemDetailsDefaults
        // fill in the RFC 9110 status-section link and reason phrase for generic failures.
        private static ProblemDetails Create(int statusCode, string detail, ProblemType? problemType = null) =>
            new()
            {
                Status = statusCode,
                Title = problemType?.Title,
                Type = problemType?.Type,
                Detail = detail
            };

        private static ProblemDetails WithServerName(ProblemDetails problemDetails, string serverName)
        {
            problemDetails.Extensions["serverName"] = serverName;
            return problemDetails;
        }
    }
}
