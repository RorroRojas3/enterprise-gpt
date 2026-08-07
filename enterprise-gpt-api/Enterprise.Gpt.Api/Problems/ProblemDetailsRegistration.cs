using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace Enterprise.Gpt.Api.Problems;

/// <summary>
/// Registers the problem details service and the enrichment shared by every problem this API writes,
/// whether it originates in an exception handler, an endpoint filter, or the status code pages
/// middleware.
/// </summary>
internal static class ProblemDetailsRegistration
{
    /// <summary>The <c>traceId</c> extension key, matching the name MVC's own problem details use.</summary>
    internal const string TraceIdExtension = "traceId";

    /// <summary>The media type RFC 9457 assigns to problem details.</summary>
    internal const string ProblemMediaType = "application/problem+json";

    /// <summary>
    /// The RFC 9110 status-section link and reason phrase for each status this API emits without an
    /// application-specific problem type.
    /// </summary>
    /// <remarks>
    /// Mirrors the framework's internal <c>ProblemDetailsDefaults</c> table. It is duplicated rather
    /// than reused because that type is <see langword="internal"/> to ASP.NET Core, and because the
    /// non-JSON <c>Accept</c> fallback path bypasses the writer that would otherwise apply it —
    /// leaving generic problems with no <c>type</c> and no <c>title</c>.
    /// </remarks>
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

    /// <summary>
    /// Registers <see cref="IProblemDetailsService"/> together with the enrichment every problem carries.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    internal static IServiceCollection AddEnterpriseProblemDetails(this IServiceCollection services)
    {
        return services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context => Enrich(context.HttpContext, context.ProblemDetails));
    }

    /// <summary>
    /// Adds the correlation and occurrence fields every problem carries, and fills in the RFC 9110
    /// <c>type</c> and <c>title</c> when the problem carries no application-specific type.
    /// </summary>
    /// <param name="httpContext">The HTTP context for the current request.</param>
    /// <param name="problemDetails">The problem details being written.</param>
    /// <remarks>
    /// Also called directly by the exception handlers and endpoint filters, whose non-JSON
    /// <c>Accept</c> paths bypass the problem details writer and so never run
    /// <c>CustomizeProblemDetails</c>. Every assignment is idempotent — the indexer rather than
    /// <c>Add</c>, and <c>??=</c> for the rest — so running on both paths is harmless.
    /// </remarks>
    internal static void Enrich(HttpContext httpContext, ProblemDetails problemDetails)
    {
        // The 32-hex W3C trace id, not Activity.Current.Id: it is the value the hosting layer puts
        // on every log scope as TraceId, so a trace id copied out of a response body finds the log
        // line it came from. TraceIdentifier only correlates within this process.
        problemDetails.Extensions[TraceIdExtension] =
            Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        problemDetails.Instance ??= httpContext.Request.Path.Value;

        if (problemDetails.Status is { } status && _statusDefaults.TryGetValue(status, out var fallback))
        {
            problemDetails.Type ??= fallback.Type;
            problemDetails.Title ??= fallback.Title;
        }
    }
}
