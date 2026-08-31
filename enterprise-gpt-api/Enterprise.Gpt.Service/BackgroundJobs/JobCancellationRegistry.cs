using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.BackgroundJobs;

/// <summary>
/// Process-local <see cref="IJobCancellationRegistry"/> backed by a dictionary of per-job
/// cancellation sources.
/// </summary>
/// <remarks>
/// Whoever removes an entry under the gate owns disposing it, and only cancelling and releasing
/// remove — which is what keeps a source from ever being observed half-disposed.
/// </remarks>
public sealed class JobCancellationRegistry(ILogger<JobCancellationRegistry> logger) : IJobCancellationRegistry, IDisposable
{
    private readonly ILogger<JobCancellationRegistry> _logger = logger;
    private readonly Dictionary<string, CancellationTokenSource> _sources = [];
    private readonly Lock _gate = new();

    /// <summary>The number of live registrations, for tests asserting nothing leaks.</summary>
    internal int ActiveRegistrationCount
    {
        get
        {
            lock (_gate)
            {
                return _sources.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Register(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var source = new CancellationTokenSource();

        lock (_gate)
        {
            // Job ids are freshly generated guids, so a collision is a caller bug rather than a race.
            if (!_sources.TryAdd(jobId, source))
            {
                source.Dispose();
                throw new InvalidOperationException($"Job {jobId} is already registered.");
            }
        }
    }

    /// <inheritdoc />
    public CancellationTokenSource? CreateLinkedTokenSource(string jobId, CancellationToken stoppingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        lock (_gate)
        {
            // A missing entry can only mean a cancel claimed it, because registration happens at
            // enqueue and release happens when the job ends. That absence is the whole record of a
            // job cancelled before it ever started.
            return _sources.TryGetValue(jobId, out var source)
                ? CancellationTokenSource.CreateLinkedTokenSource(source.Token, stoppingToken)
                : null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        CancellationTokenSource? claimed;

        lock (_gate)
        {
            if (!_sources.Remove(jobId, out claimed))
            {
                return false;
            }
        }

        try
        {
            // Outside the gate, because cancelling runs the callbacks the pipeline registered — an
            // HTTP abort, a command cancellation round trip — and none of that belongs under a lock
            // every other cancel is waiting on.
            await claimed.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A callback that throws belongs to the work being cancelled, not to the caller asking.
            _logger.LogWarning(ex, "A cancellation callback for job {JobId} threw", jobId);
        }

        // After cancelling, never before: disposing an uncancelled source strands the linked child.
        claimed.Dispose();

        return true;
    }

    /// <inheritdoc />
    public void Release(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        CancellationTokenSource? claimed;

        lock (_gate)
        {
            if (!_sources.Remove(jobId, out claimed))
            {
                return;
            }
        }

        claimed.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Dropped but not cancelled: a source an in-flight job still links from would fault that job
        // unwind, and one whose wait handle was never touched holds nothing to leak.
        lock (_gate)
        {
            _sources.Clear();
        }
    }
}
