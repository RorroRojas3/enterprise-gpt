using System.Diagnostics;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Api.Problems.Models;
using Microsoft.AspNetCore.Mvc;

namespace Enterprise.Gpt.Api.Problems;

/// <summary>
/// Registers problem details and the enrichment shared by every problem, whether it comes from an
/// exception handler, an endpoint filter, or the status code pages middleware.
/// </summary>
internal static class ProblemDetailsRegistration
{
    /// <summary>The <c>traceId</c> extension key, matching the name MVC's own problem details use.</summary>
    internal const string TraceIdExtension = "traceId";

    internal const string ProblemMediaType = "application/problem+json";

    // A copy of ASP.NET Core's internal ProblemDetailsDefaults, which the non-JSON Accept fallback bypasses.
    private static readonly Dictionary<int, ProblemType> _statusDefaults = new()
    {
        [StatusCodes.Status400BadRequest] = new("https://tools.ietf.org/html/rfc9110#section-15.5.1", "Bad Request"),
        [StatusCodes.Status401Unauthorized] = new("https://tools.ietf.org/html/rfc9110#section-15.5.2", "Unauthorized"),
        [StatusCodes.Status403Forbidden] = new("https://tools.ietf.org/html/rfc9110#section-15.5.4", "Forbidden"),
        [StatusCodes.Status404NotFound] = new("https://tools.ietf.org/html/rfc9110#section-15.5.5", "Not Found"),
        [StatusCodes.Status409Conflict] = new("https://tools.ietf.org/html/rfc9110#section-15.5.10", "Conflict"),
        [StatusCodes.Status413PayloadTooLarge] = new("https://tools.ietf.org/html/rfc9110#section-15.5.14", "Content Too Large"),
        [StatusCodes.Status500InternalServerError] = new("https://tools.ietf.org/html/rfc9110#section-15.6.1", "An error occurred while processing your request."),
        [StatusCodes.Status502BadGateway] = new("https://tools.ietf.org/html/rfc9110#section-15.6.3", "Bad Gateway")
    };

    #region Public static methods

    internal static IServiceCollection AddEnterpriseGptProblemDetails(this IServiceCollection services)
    {
        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context => Enrich(context.HttpContext, context.ProblemDetails));
    }

    /// <summary>
    /// Adds the correlation fields every problem carries and the RFC 9110 <c>type</c>/<c>title</c> fallback.
    /// </summary>
    /// <remarks>
    /// Also called directly on non-JSON <c>Accept</c> paths, which skip <c>CustomizeProblemDetails</c>;
    /// every assignment is idempotent, so running on both paths is harmless.
    /// </remarks>
    internal static void Enrich(HttpContext httpContext, ProblemDetails problemDetails)
    {
        // The W3C trace id, not Activity.Current.Id: hosting logs it as TraceId on every scope, so an id
        // copied from a response body finds its log line. TraceIdentifier only correlates in-process.
        problemDetails.Extensions[TraceIdExtension] =
            Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        problemDetails.Instance ??= httpContext.Request.Path.Value;

        if (problemDetails.Status is { } status && _statusDefaults.TryGetValue(status, out var fallback))
        {
            problemDetails.Type ??= fallback.Type;
            problemDetails.Title ??= fallback.Title;
        }
    }

    #endregion
}
