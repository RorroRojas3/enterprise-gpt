using Microsoft.AspNetCore.Diagnostics;
using Enterprise.Gpt.Dto;
using System.Net;
using System.Text.Json;

namespace Enterprise.Gpt.Api.ExceptionHandlers
{
    /// <summary>
    /// Fallback exception handler that maps the remaining exception types
    /// (<see cref="ArgumentException"/>, <see cref="ArgumentNullException"/>,
    /// <see cref="InvalidOperationException"/>, <see cref="KeyNotFoundException"/>) and any
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
                InvalidOperationException => new ErrorDto
                {
                    StatusCode = HttpStatusCode.BadRequest,
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
