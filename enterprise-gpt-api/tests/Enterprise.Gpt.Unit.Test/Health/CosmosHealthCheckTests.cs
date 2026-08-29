using Enterprise.Gpt.Api.Health;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Health;

/// <summary>
/// Covers the readiness probe for the transcript account, including what its result may not carry.
/// </summary>
public sealed class CosmosHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_AReachableAccount_ReportsHealthy()
    {
        var client = Substitute.For<CosmosClient>();
        client.ReadAccountAsync().Returns(Task.FromResult<AccountProperties>(null!));

        var result = await new CosmosHealthCheck(client)
            .CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// The description reaches an anonymous response body, and a Cosmos failure message can name the
    /// account endpoint and its key.
    /// </summary>
    [Fact]
    public async Task CheckHealthAsync_AnUnreachableAccount_ReportsTheFailureStatusWithoutDescribingIt()
    {
        var client = Substitute.For<CosmosClient>();
        client.ReadAccountAsync().ThrowsAsync(new HttpRequestException("AccountKey=super-secret"));

        var result = await new CosmosHealthCheck(client)
            .CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Description);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task CheckHealthAsync_ACancelledProbe_ReportsTheFailureStatusRatherThanThrowing()
    {
        var client = Substitute.For<CosmosClient>();
        client.ReadAccountAsync().Returns(new TaskCompletionSource<AccountProperties>().Task);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await new CosmosHealthCheck(client).CheckHealthAsync(Context(), cancellation.Token);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(
            "cosmos",
            _ => throw new InvalidOperationException("The factory is never used here."),
            HealthStatus.Unhealthy,
            tags: null)
    };
}
