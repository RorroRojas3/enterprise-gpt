using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Reports;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for reporting over the conversation usage audit trail.
/// </summary>
/// <remarks>
/// Administrator-gated at the route rather than owner-scoped like the conversation endpoints:
/// this reports on everybody's spend, so there is no caller whose own data it could be narrowed
/// to, and a 404 would be the wrong answer to a request nobody is allowed to make.
/// </remarks>
public static class ReportEndpoints
{
    /// <summary>
    /// Maps the <c>api/reports</c> endpoint group.
    /// </summary>
    /// <param name="app">The route builder to map the group onto.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/reports")
            .RequireAuthorization()
            .WithTags("Reports")
            // Every route in the group is authorized, so the challenge applies uniformly.
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("usage", GetUsageReportAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    // Query parameters follow the service, as on `GET api/users`: they need defaults so every one
    // of them stays optional, and C# requires optional parameters last. A window this route
    // refuses is a validation problem from the service rather than a clamp — see
    // `ReportService.ResolveWindow` for why this one is not clamped like a page size.
    internal static async Task<Ok<UsageReportDto>> GetUsageReportAsync(
        IReportService reportService,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? groupBy = null,
        CancellationToken cancellationToken = default)
    {
        var response = await reportService.GetUsageReportAsync(from, to, groupBy, cancellationToken);
        return TypedResults.Ok(response);
    }
}
