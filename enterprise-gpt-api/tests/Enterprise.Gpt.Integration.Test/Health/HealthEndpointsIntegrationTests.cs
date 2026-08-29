using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Health;

/// <summary>
/// Covers what a unit test cannot show about the probes: that they are routed, that they answer
/// without a bearer token, and what an anonymous caller is told when a dependency is down.
/// </summary>
/// <remarks>
/// Nothing here asserts a readiness <em>status</em>: this host runs SQL Server for real and points
/// Cosmos at the emulator address, which may or may not be listening on the machine running the suite.
/// The contract that holds either way is the shape of the answer.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HealthEndpointsIntegrationTests(IntegrationTestFixture fixture)
{
    private readonly IntegrationTestFixture _fixture = fixture;

    /// <summary>
    /// A platform probe carries no token, and an endpoint that answers 401 to the load balancer is
    /// indistinguishable from one that is down.
    /// </summary>
    [Fact]
    public async Task Liveness_Anonymous_AnswersWithoutTouchingADependency()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The status code has to agree with the reported status, because that pairing is the whole
    /// contract a load balancer reads.
    /// </summary>
    [Fact]
    public async Task Readiness_Anonymous_ReportsAnAggregateStatusMatchingItsStatusCode()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var status = body.RootElement.GetProperty("status").GetString();
        Assert.Contains(status, Enum.GetNames<HealthStatus>());

        var expected = status == nameof(HealthStatus.Unhealthy)
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.OK;
        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// Which dependency is which is a deployment's topology, and this route is anonymous — that detail
    /// belongs in the log, where the readiness handler writes it.
    /// </summary>
    [Fact]
    public async Task Readiness_Anonymous_NamesNoDependencyInItsBody()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("cosmos", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duration", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Liveness_ASuccessfulProbe_IsNotWrittenToTheAccessLog()
    {
        _fixture.Factory.ClearRequestLogs();

        using var client = _fixture.Factory.CreateAnonymousClient();
        await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Empty(_fixture.Factory.GetRequestLogs());
    }
}
