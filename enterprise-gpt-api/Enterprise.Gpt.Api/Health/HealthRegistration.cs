using Enterprise.Gpt.Repository.Cosmos;
using Enterprise.Gpt.Repository.Sql;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Enterprise.Gpt.Api.Health;

/// <remarks>
/// Two routes: a host that cannot reach its database must fail readiness without being recycled, which a
/// liveness failure would cause.
/// </remarks>
internal static class HealthRegistration
{
    private const string ReadyTag = "ready";

    private static readonly string[] ReadyTags = [ReadyTag];

    // Fail fast: a platform-side timeout reads as an ambiguous failure rather than an unready instance.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    #region Private methods

    // Aggregate status only: which dependency failed must not reach an anonymous caller, so ReadinessProbe
    // logs it. Deliberately not problem+json: a load balancer reads this, not an API client.
    private static async Task<IResult> ReadinessAsync(
        ReadinessProbe probe,
        CancellationToken cancellationToken)
    {
        var report = await probe.CheckAsync(IsReadiness, cancellationToken).ConfigureAwait(false);

        return Results.Json(
            new { status = report.Status.ToString() },
            statusCode: report.Status is HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK);
    }

    private static bool IsReadiness(HealthCheckRegistration registration) =>
        registration.Tags.Contains(ReadyTag);

    #endregion

    #region Public static methods

    internal static IServiceCollection AddEnterpriseGptHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddEnterpriseGptSqlHealthCheck("sql", ReadyTags)
            .AddEnterpriseGptCosmosHealthCheck("cosmos", ReadyTags);

        // Applied afterwards: AddDbContextCheck takes no timeout, which would leave the SQL probe bounded only
        // by SqlClient's connect timeout, three times this ceiling, on an anonymous route.
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations.Where(IsReadiness))
            {
                registration.Timeout = ProbeTimeout;
            }
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ReadinessProbe>();

        return services;
    }

    /// <remarks>
    /// Both are anonymous: a platform probe carries no bearer token, and a 401 to the load balancer is
    /// indistinguishable from being down.
    /// </remarks>
    internal static IEndpointRouteBuilder MapEnterpriseGptHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            // No checks at all: liveness has to stay answerable while a dependency is down, or a
            // database outage becomes a restart loop.
            Predicate = _ => false
        }).AllowAnonymous().ExcludeFromDescription().WithName("Liveness");

        // By hand: MapHealthChecks cannot serve a cached report (see ReadinessProbe). HEAD is explicit because
        // routing does not fall it back to GET, and uptime monitors default to it.
        app.MapMethods("/health/ready", [HttpMethods.Get, HttpMethods.Head], ReadinessAsync)
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("Readiness");

        return app;
    }

    #endregion
}
