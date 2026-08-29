using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Enterprise.Gpt.Api.Health;

/// <summary>
/// Reports whether the transcript account is reachable.
/// </summary>
/// <remarks>
/// Reads the account rather than a container: a container read charges request units on the
/// fastest-growing partition in the deployment, and a probe runs on an interval the operator chooses
/// rather than one this application controls.
/// </remarks>
/// <param name="client">The transcript account's client.</param>
internal sealed class CosmosHealthCheck(CosmosClient client) : IHealthCheck
{
    private readonly CosmosClient _client = client;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // ReadAccountAsync takes no token and the SDK retries a dead endpoint for tens of seconds, so
        // WaitAsync is what makes the registration's timeout the probe's real ceiling. It bounds the
        // wait and not the work, which is why the abandoned call's fault is observed rather than left
        // to surface later as an unobserved task exception.
        var read = _client.ReadAccountAsync();
        _ = read.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);

        try
        {
            await read.WaitAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: exception);
        }
    }
}
