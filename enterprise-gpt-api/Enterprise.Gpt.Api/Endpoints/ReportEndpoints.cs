using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Dto.Reports;
using Enterprise.Gpt.Service.Reports;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>Reporting over the conversation usage audit trail.</summary>
/// <remarks>
/// Administrator-gated rather than owner-scoped: it reports on everybody's spend, so there is no caller's own
/// data to narrow it to.
/// </remarks>
public static class ReportEndpoints
{
    #region Public static methods

    /// <summary>Maps the <c>api/reports</c> endpoint group.</summary>
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/reports")
            .RequireAuthorization()
            .WithTags("Reports")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("usage", GetUsageReportAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    // A refused window is a validation problem rather than a clamp; `ReportService.ResolveWindow` says why.
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

    #endregion
}
