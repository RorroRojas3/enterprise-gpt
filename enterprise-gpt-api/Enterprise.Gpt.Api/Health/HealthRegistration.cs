using Enterprise.Gpt.Repository;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Enterprise.Gpt.Api.Health;

/// <summary>
/// Registers and maps the liveness and readiness endpoints.
/// </summary>
/// <remarks>
/// Two routes rather than one because they answer different questions: a host that is up but cannot
/// reach its database must fail readiness without being recycled, which is what a liveness failure
/// would cause.
/// </remarks>
internal static class HealthRegistration
{
    /// <summary>Tag carried by the checks that gate readiness.</summary>
    private const string ReadyTag = "ready";

    private static readonly string[] ReadyTags = [ReadyTag];

    /// <summary>Ceiling on a single dependency probe.</summary>
    /// <remarks>
    /// An unreachable dependency has to fail fast rather than leave the platform to time the request
    /// out, which it would report as an ambiguous failure rather than as an unready instance.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Registers the readiness checks and the cache in front of them.</summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    internal static IServiceCollection AddEnterpriseHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<EnterpriseGptDbContext>("sql", tags: ReadyTags)
            .AddCheck<CosmosHealthCheck>("cosmos", tags: ReadyTags);

        // Applied afterwards rather than per registration: AddDbContextCheck takes no timeout, so the
        // relational probe would otherwise be bounded only by SqlClient's own connect timeout — three
        // times the ceiling above, on an anonymous route.
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations.Where(IsReadiness))
            {
                registration.Timeout = ProbeTimeout;
            }
        });

        services.AddSingleton<ReadinessProbe>();

        return services;
    }

    /// <summary>Maps <c>/health</c> and <c>/health/ready</c>.</summary>
    /// <param name="app">The endpoint route builder to map into.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    /// <remarks>
    /// Both are anonymous. A platform probe carries no bearer token, and an endpoint that answers 401
    /// to the load balancer is indistinguishable from one that is down.
    /// </remarks>
    internal static IEndpointRouteBuilder MapEnterpriseHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            // No checks at all: liveness has to stay answerable while a dependency is down, or a
            // database outage becomes a restart loop.
            Predicate = _ => false
        }).AllowAnonymous().ExcludeFromDescription().WithName("Liveness");

        // Mapped by hand rather than through MapHealthChecks, which offers no way to serve a cached
        // report — see ReadinessProbe for why an anonymous route must not probe per request. HEAD is
        // listed explicitly because routing does not fall it back to GET, and uptime monitors default
        // to it; MapHealthChecks above accepts every verb already.
        app.MapMethods("/health/ready", [HttpMethods.Get, HttpMethods.Head], ReadinessAsync)
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("Readiness");

        return app;
    }

    /// <summary>
    /// Answers with the aggregate status and nothing else.
    /// </summary>
    /// <remarks>
    /// Which dependency failed is what an operator wants and what an anonymous caller must not be told;
    /// <see cref="ReadinessProbe"/> logs it instead. The body is deliberately not
    /// <c>application/problem+json</c> — this one is read by a load balancer, not by a client of the API.
    /// </remarks>
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
}
