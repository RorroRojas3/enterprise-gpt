using Enterprise.Gpt.Api.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Health;

/// <summary>
/// Covers the registration wiring, which is where a readiness probe silently loses its ceiling.
/// </summary>
public sealed class HealthRegistrationTests
{
    /// <summary>
    /// The one way this stops working is a refactor that runs the Configure callback before the checks
    /// are added, which no other test would notice.
    /// </summary>
    [Fact]
    public void AddEnterpriseHealthChecks_EveryReadinessCheck_CarriesTheProbeTimeout()
    {
        var registrations = Registrations();

        Assert.NotEmpty(registrations);
        Assert.All(registrations, registration => Assert.Equal(TimeSpan.FromSeconds(5), registration.Timeout));
    }

    /// <summary>
    /// AddDbContextCheck takes no timeout of its own, so the relational probe is the one that would
    /// otherwise fall back to SqlClient's connect timeout.
    /// </summary>
    [Fact]
    public void AddEnterpriseHealthChecks_TheRegisteredChecks_AreTheRelationalAndTranscriptStores()
    {
        Assert.Equal(["cosmos", "sql"], Registrations().Select(registration => registration.Name).Order());
    }

    private static ICollection<HealthCheckRegistration> Registrations()
    {
        var services = new ServiceCollection();
        services.AddEnterpriseHealthChecks();

        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;
    }
}
