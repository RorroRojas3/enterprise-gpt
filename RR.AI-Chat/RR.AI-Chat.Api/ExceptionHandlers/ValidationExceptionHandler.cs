using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using RR.AI_Chat.Dto;
using System.Net;
using System.Text.Json;

namespace RR.AI_Chat.Api.ExceptionHandlers
{
    /// <summary>
    /// Handles <see cref="ValidationException"/> instances thrown by FluentValidation by
    /// emitting an HTTP 400 Bad Request response that lists every rule violation in the
    /// <see cref="ErrorDto.Errors"/> collection.
    /// </summary>
    /// <param name="logger">Logger instance for recording validation failures.</param>
    internal sealed class ValidationExceptionHandler(
        ILogger<ValidationExceptionHandler> logger) : IExceptionHandler
    {
        private readonly ILogger<ValidationExceptionHandler> _logger = logger;

        /// <summary>
        /// Attempts to handle the specified exception by writing an <see cref="ErrorDto"/>
        /// with HTTP 400 when the exception is a FluentValidation <see cref="ValidationException"/>.
        /// </summary>
        /// <param name="httpContext">The HTTP context for the current request.</param>
        /// <param name="exception">The exception to handle.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>
        /// <see langword="true"/> if the exception was a <see cref="ValidationException"/> and
        /// has been handled; otherwise, <see langword="false"/>.
        /// </returns>
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (exception is not ValidationException validationException)
            {
                return false;
            }

            _logger.LogError(exception,
                "An error occurred: {Message}. TraceId: {TraceId}",
                exception.Message,
                httpContext.TraceIdentifier);

            var error = new ErrorDto
            {
                StatusCode = HttpStatusCode.BadRequest,
                Errors = [.. validationException.Errors.Select(e => e.ErrorMessage)],
                TraceId = httpContext.TraceIdentifier,
                Timestamp = DateTimeOffset.UtcNow
            };

            httpContext.Response.ContentType = "application/json";
            httpContext.Response.StatusCode = (int)error.StatusCode;
            await httpContext.Response.WriteAsync(JsonSerializer.Serialize(error), cancellationToken);

            return true;
        }
    }
}
