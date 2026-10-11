using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Enterprise.Gpt.Repository.Cosmos.HealthChecks;

/// <summary>
/// Reports whether the transcript account is reachable.
/// </summary>
/// <remarks>
/// Reads the account, not a container: a container read charges RUs on an operator-chosen interval.
/// </remarks>
internal sealed class CosmosHealthCheck(CosmosClient client) : IHealthCheck
{
    private readonly CosmosClient _client = client;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // ReadAccountAsync takes no token and retries a dead endpoint for tens of seconds, so WaitAsync
        // bounds the wait; the abandoned call's fault is observed to avoid an unobserved task exception.
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
