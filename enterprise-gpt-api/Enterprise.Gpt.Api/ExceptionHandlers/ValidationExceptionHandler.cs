using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Enterprise.Gpt.Api.Problems;

namespace Enterprise.Gpt.Api.ExceptionHandlers
{
    /// <summary>
    /// Handles <see cref="ValidationException"/> instances thrown by FluentValidation by emitting an
    /// HTTP 400 Bad Request <see cref="HttpValidationProblemDetails"/> response whose
    /// <see cref="HttpValidationProblemDetails.Errors"/> dictionary is keyed by the failing property.
    /// </summary>
    /// <param name="problemDetailsService">Writes the content-negotiated problem details response.</param>
    /// <param name="logger">Logger instance for recording validation failures.</param>
    internal sealed class ValidationExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<ValidationExceptionHandler> logger) : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService = problemDetailsService;
        private readonly ILogger<ValidationExceptionHandler> _logger = logger;

        /// <summary>
        /// Attempts to handle the specified exception by writing an
        /// <see cref="HttpValidationProblemDetails"/> with HTTP 400 when the exception is a
        /// FluentValidation <see cref="ValidationException"/>.
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

            // See GlobalExceptionHandler: the trace id comes from the hosting log scope, not the
            // template.
            _logger.LogError(exception, "An error occurred: {Message}", exception.Message);

            // Belt and braces, as in GlobalExceptionHandler: ExceptionHandlerMiddleware already
            // skips every handler once the response has started.
            if (httpContext.Response.HasStarted)
            {
                return true;
            }

            // Grouped because one property can fail several rules at once — NotEmpty and
            // MaximumLength on the same field, for instance.
            var errors = validationException.Errors
                .GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(failure => failure.ErrorMessage).ToArray(),
                    StringComparer.Ordinal);

            // Detail stays null: the errors dictionary already carries every message, and
            // ValidationException.Message is a multi-line concatenation meant for logs.
            // Title is set explicitly even though the constructor already supplies the same string:
            // leaving it implicit made ProblemTypes.Validation.Title dead, so editing it would have
            // changed nothing on the wire.
            var problemDetails = new HttpValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Type = ProblemTypes.Validation.Type,
                Title = ProblemTypes.Validation.Title
            };

            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

            if (!await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problemDetails,
                Exception = exception
            }))
            {
                ProblemDetailsRegistration.Enrich(httpContext, problemDetails);
                await httpContext.Response.WriteAsJsonAsync(
                    problemDetails,
                    options: null,
                    contentType: ProblemDetailsRegistration.ProblemMediaType,
                    cancellationToken);
            }

            return true;
        }
    }
}
