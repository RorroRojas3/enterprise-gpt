using Microsoft.AspNetCore.Diagnostics;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Exceptions;
using System.Net;
using System.Text.Json;

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
    /// otherwise unhandled exception to a standardized <see cref="ErrorDto"/> response.
    /// Always returns <see langword="true"/> so any exception receives a consistent payload.
    /// </summary>
    /// <param name="logger">Logger instance for recording error information.</param>
    internal sealed class GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
    {
        private readonly ILogger<GlobalExceptionHandler> _logger = logger;

        /// <summary>
        /// Handles the specified exception by logging it and writing a standardized
        /// <see cref="ErrorDto"/> response to the HTTP response stream.
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
            _logger.LogError(exception,
                "An error occurred: {Message}. TraceId: {TraceId}",
                exception.Message,
                httpContext.TraceIdentifier);

            // Mirrors OperationCanceledExceptionHandler: once the response has started — a chat
            // stream that faults after its first fragment, say — the status and headers are
            // immutable and appending a JSON envelope would corrupt the body the client is already
            // reading. Setting either would itself throw and mask the original exception.
            if (httpContext.Response.HasStarted)
            {
                return true;
            }

            var error = MapToError(httpContext, exception);

            httpContext.Response.ContentType = "application/json";
            httpContext.Response.StatusCode = (int)error.StatusCode;
            await httpContext.Response.WriteAsync(JsonSerializer.Serialize(error), cancellationToken);

            return true;
        }

        private static ErrorDto MapToError(HttpContext context, Exception exception) =>
            exception switch
            {
                ArgumentNullException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.BadRequest,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                ArgumentException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.BadRequest,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                // Ordered before InvalidOperationException only for readability; the type is
                // unrelated, but contention is a temporary state the caller can retry out of,
                // so it must not collapse into the 400 bucket alongside validation failures.
                ConversationBusyException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.Conflict,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                InvalidOperationException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.BadRequest,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                NotFoundException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.NotFound,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                KeyNotFoundException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.NotFound,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                ForbiddenException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.Forbidden,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                // 403, not 401: a 401 would send clients into token-refresh loops that cannot
                // fix a consent or Conditional Access requirement.
                McpAuthorizationRequiredException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.Forbidden,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                McpServerUnavailableException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.BadGateway,
                    Errors = [exception.Message],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                },
                _ => new ErrorDto
                {
                    StatusCode = HttpStatusCode.InternalServerError,
                    Errors = ["An unexpected internal server error occurred."],
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                }
            };
    }
}
