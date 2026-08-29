using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Enterprise.Gpt.Api.Health;

/// <summary>
/// Runs the readiness checks at most once per <see cref="Ttl"/>, however often the route is called.
/// </summary>
/// <remarks>
/// The route is anonymous because a platform probe carries no token, which makes it a request anyone
/// can make. Without this, each one would open a SQL connection and read the Cosmos account — an
/// unauthenticated caller could drive both backends for the cost of an HTTP GET. Single-flight rather
/// than a plain expiry, so a burst arriving on a cold entry produces one probe and not one each.
/// </remarks>
/// <param name="health">Runs the registered checks.</param>
/// <param name="timeProvider">Supplies the monotonic timestamps a snapshot ages against.</param>
/// <param name="logger">Receives which dependency failed, which the anonymous response cannot carry.</param>
internal sealed class ReadinessProbe(
    HealthCheckService health,
    TimeProvider timeProvider,
    ILogger<ReadinessProbe> logger)
{
    /// <summary>How long one report is served for.</summary>
    /// <remarks>Below every platform's probe interval, so a real state change is never hidden for long.</remarks>
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);

    private readonly HealthCheckService _health = health;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ReadinessProbe> _logger = logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Snapshot? _snapshot;

    /// <summary>Returns the current report, running the checks only when the last one has aged out.</summary>
    /// <param name="predicate">Selects the checks that gate readiness.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate, and nothing beyond it.</param>
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

            // Not the caller's token. This probe is shared, so a caller that walks away — or a platform
            // whose own probe timeout is shorter than the registrations' — must not cancel work the
            // others are waiting for, which would leave the cache cold under exactly the degraded
            // conditions it exists for. Each registration carries its own timeout, so the work is bounded.
            var report = await _health.CheckHealthAsync(predicate, CancellationToken.None).ConfigureAwait(false);

            // Logged here rather than per reader: which dependency failed is what the anonymous response
            // deliberately omits, and writing it once per probe rather than once per request is what
            // keeps that detail from becoming its own amplification path into log ingestion.
            LogFailures(report);

            _snapshot = new Snapshot(report, _timeProvider.GetTimestamp());

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LogFailures(HealthReport report)
    {
        if (report.Status is HealthStatus.Healthy)
        {
            return;
        }

        foreach (var (name, entry) in report.Entries.Where(entry => entry.Value.Status is not HealthStatus.Healthy))
        {
            _logger.LogError(
                entry.Exception,
                "Readiness check {CheckName} reported {CheckStatus} after {ElapsedMilliseconds} ms",
                name,
                entry.Status,
                entry.Duration.TotalMilliseconds);
        }
    }

    private HealthReport? TryReadFresh() =>
        _snapshot is { } snapshot && _timeProvider.GetElapsedTime(snapshot.TakenAt) < Ttl
            ? snapshot.Report
            : null;

    /// <summary>
    /// Report and timestamp published as one reference, so the lock-free read cannot pair a fresh
    /// timestamp with a stale report.
    /// </summary>
    private sealed record Snapshot(HealthReport Report, long TakenAt);
}
