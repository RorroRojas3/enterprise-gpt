using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Api.Problems.Constants;
using Enterprise.Gpt.Api.Problems.Models;
using Enterprise.Gpt.Service.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Enterprise.Gpt.Api.ExceptionHandlers;

/// <summary>
/// Turns every exception that escapes an endpoint into an RFC 9457 problem details response.
/// </summary>
/// <remarks>
/// A <see cref="ProblemException"/> carries its own status and problem type, so a new one maps without
/// this handler naming it. Anything unmapped is a 500 whose message never reaches the caller.
/// </remarks>
/// <param name="problemDetailsService">Writes the content-negotiated problem details response.</param>
/// <param name="logger">Records server faults.</param>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService = problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> _logger = logger;

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Only a client that actually went away gets 499. A timeout inside the request is a server
        // fault and falls through to the 500 arm.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogRequestCancelled(_logger);

            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }

            return true;
        }

        var problemDetails = MapToProblemDetails(exception);
        var statusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        // .NET 10 no longer logs an exception an IExceptionHandler handled; 4xx outcomes are expected.
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            LogServerError(_logger, exception, exception.Message);
        }

        // ExceptionHandlerMiddleware already skips handlers once the response has started; the status and
        // headers are immutable here, and assigning either would mask the original exception.
        if (httpContext.Response.HasStarted)
        {
            return true;
        }

        // The writer reads Response.StatusCode, which the middleware has already set to 500.
        httpContext.Response.StatusCode = statusCode;

        if (!await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        }))
        {
            // The writer declines media types outside JSON. A caller sending Accept: text/html still gets
            // a body, and Enrich runs explicitly because this path never reaches CustomizeProblemDetails.
            ProblemDetailsRegistration.Enrich(httpContext, problemDetails);
            await httpContext.Response.WriteAsJsonAsync(
                problemDetails,
                options: null,
                contentType: ProblemDetailsRegistration.ProblemMediaType,
                cancellationToken);
        }

        return true;
    }

    #region Private methods

    private static ProblemDetails MapToProblemDetails(Exception exception) =>
        exception switch
        {
            ValidationException validation => ValidationProblems.Create(validation.Errors),
            // Kestrel throws this when a chunked upload exceeds the limit MaxUploadSizeEndpointFilter
            // lowers; its StatusCode is already the right client error (413 there).
            BadHttpRequestException badRequest => Create(badRequest.StatusCode, badRequest.Message),
            ProblemException problem => ToProblem(problem),
            // The real message is suppressed: an unmapped exception may carry connection strings, file
            // paths, or query text.
            _ => Create(StatusCodes.Status500InternalServerError, "An unexpected internal server error occurred.")
        };

    private static ProblemDetails ToProblem(ProblemException exception)
    {
        // An unmapped code degrades to the RFC 9110 default rather than failing inside the handler.
        var problemType = exception.ProblemCode is { } problemCode ? ProblemTypes.ForCode(problemCode) : null;
        var problemDetails = Create((int)exception.StatusCode, exception.Message, problemType);

        foreach (var (name, value) in exception.Extensions)
        {
            problemDetails.Extensions[name] = value;
        }

        return problemDetails;
    }

    // A null problemType leaves Type and Title unset, so the RFC 9110 status-section defaults apply.
    private static ProblemDetails Create(int statusCode, string detail, ProblemType? problemType = null) =>
        new()
        {
            Status = statusCode,
            Title = problemType?.Title,
            Type = problemType?.Type,
            Detail = detail
        };

    #endregion

    #region Logging

    // The trace id stays out of the template: the hosting layer already puts it on every log scope.
    [LoggerMessage(Level = LogLevel.Error, Message = "An error occurred: {Message}")]
    private static partial void LogServerError(ILogger logger, Exception exception, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request was cancelled.")]
    private static partial void LogRequestCancelled(ILogger logger);

    #endregion
}
