using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Enterprise.Gpt.Api.Health;

/// <summary>
/// Runs the readiness checks at most once per <see cref="Ttl"/>, however often the route is called.
/// </summary>
/// <remarks>
/// The route is anonymous, so otherwise any caller could drive SQL and Cosmos for the cost of a GET.
/// Single-flight, so a burst arriving on a cold entry produces one probe.
/// </remarks>
internal sealed partial class ReadinessProbe(
    HealthCheckService health,
    TimeProvider timeProvider,
    ILogger<ReadinessProbe> logger) : IDisposable
{
    /// <summary>
    /// How long one report is served; below every platform's probe interval, so a real change is never
    /// hidden for long.
    /// </summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);

    private readonly HealthCheckService _health = health;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ReadinessProbe> _logger = logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Snapshot? _snapshot;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Returns the current report, running the checks only when the last one has aged out.</summary>
    /// <remarks><paramref name="cancellationToken"/> cancels only the wait for the gate.</remarks>
    internal async Task<HealthReport> CheckAsync(
        Func<HealthCheckRegistration, bool> predicate,
        CancellationToken cancellationToken)
    {
        if (TryReadFresh() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-checked inside the gate: everyone queued behind the caller that ran the probe wants
            // its result, not another probe.
            if (TryReadFresh() is { } filled)
            {
                return filled;
            }

            // Not the caller's token: the probe is shared, and a caller walking away must not leave the cache
            // cold exactly when degraded. Each registration's own timeout bounds the work.
            var report = await _health.CheckHealthAsync(predicate, CancellationToken.None).ConfigureAwait(false);

            // Once per probe, not per request, so the detail the anonymous response omits cannot amplify into
            // log ingestion.
            LogFailures(report);

            _snapshot = new Snapshot(report, _timeProvider.GetTimestamp());

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    // One reference, so the lock-free read cannot pair a fresh timestamp with a stale report.
    private sealed record Snapshot(HealthReport Report, long TakenAt);

    #region Private methods

    private void LogFailures(HealthReport report)
    {
        if (report.Status is HealthStatus.Healthy)
        {
            return;
        }

        foreach (var (name, entry) in report.Entries.Where(entry => entry.Value.Status is not HealthStatus.Healthy))
        {
            LogReadinessCheckReported(_logger, entry.Exception, name, entry.Status, entry.Duration.TotalMilliseconds);
        }
    }

    private HealthReport? TryReadFresh() =>
        _snapshot is { } snapshot && _timeProvider.GetElapsedTime(snapshot.TakenAt) < Ttl
            ? snapshot.Report
            : null;

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "Readiness check {CheckName} reported {CheckStatus} after {ElapsedMilliseconds} ms")]
    private static partial void LogReadinessCheckReported(
        ILogger logger, Exception? exception, string checkName, HealthStatus checkStatus, double elapsedMilliseconds);

    #endregion
}
