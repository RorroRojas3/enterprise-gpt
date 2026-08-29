using Microsoft.AspNetCore.Diagnostics;

namespace Enterprise.Gpt.Api.ExceptionHandlers
{
    /// <summary>
    /// Handles <see cref="OperationCanceledException"/> instances by mapping them to the
    /// non-standard HTTP 499 (client closed request) status when the response has not yet
    /// started. No body is written so streaming responses (SSE) remain intact when a client
    /// disconnects mid-stream.
    /// </summary>
    /// <param name="logger">Logger instance for recording cancellations.</param>
    internal sealed class OperationCanceledExceptionHandler(
        ILogger<OperationCanceledExceptionHandler> logger) : IExceptionHandler
    {
        private readonly ILogger<OperationCanceledExceptionHandler> _logger = logger;

        /// <summary>
        /// Attempts to handle the specified exception by setting HTTP 499 when the exception
        /// is an <see cref="OperationCanceledException"/> and the response has not started.
        /// </summary>
        /// <param name="httpContext">The HTTP context for the current request.</param>
        /// <param name="exception">The exception to handle.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>
        /// <see langword="true"/> if the exception was an <see cref="OperationCanceledException"/>
        /// and has been handled; otherwise, <see langword="false"/>.
        /// </returns>
        public ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (exception is not OperationCanceledException)
            {
                return ValueTask.FromResult(false);
            }

            // See GlobalExceptionHandler: the trace id comes from the hosting log scope, not the
            // template. TraceIdentifier under that same name only correlates within this process, and
            // disagreed with the id every problem response hands the caller.
            _logger.LogInformation("Request was cancelled.");

            // If the response has already started (e.g. mid-stream SSE), headers are immutable.
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }

            return ValueTask.FromResult(true);
        }
    }
}
