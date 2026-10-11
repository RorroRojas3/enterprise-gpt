using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.BackgroundJobs;

/// <summary>
/// Tracks the cancellation source of every queued and running background job, so a caller can call
/// one off while it is still doing work.
/// </summary>
/// <remarks>
/// Process-local, like the queue and the status store beside it: a job only exists on the instance
/// that accepted its upload. See <c>docs/documents/ingestion.md</c> before making any of the three
/// distributed.
/// </remarks>
public interface IJobCancellationRegistry
{
    /// <summary>
    /// Records a cancellation source for a job that is being enqueued.
    /// </summary>
    /// <remarks>
    /// Called at enqueue rather than when the job starts, so a job cancelled while it is still in the
    /// queue — or while a full queue blocks the caller — has somewhere to record that.
    /// </remarks>
    /// <param name="jobId">The job identifier.</param>
    void Register(string jobId);

    /// <summary>
    /// Creates the token a job runs under, linking its own cancellation source to the host stopping token.
    /// </summary>
    /// <remarks>
    /// Linking happens inside the registry gate: reading a source and linking it in two steps would let
    /// a concurrent cancel dispose it in between, and the link reads the source internals.
    /// </remarks>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="stoppingToken">The host stopping token.</param>
    /// <returns>
    /// The linked source, which the caller owns and must dispose, or <see langword="null"/> when the
    /// job was cancelled before it started.
    /// </returns>
    CancellationTokenSource? CreateLinkedTokenSource(string jobId, CancellationToken stoppingToken);

    /// <summary>
    /// Requests cancellation of a queued or running job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns><see langword="true"/> when a live registration was claimed and signalled.</returns>
    Task<bool> CancelAsync(string jobId);

    /// <summary>
    /// Drops a finished job registration.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    void Release(string jobId);
}

/// <summary>
/// Process-local <see cref="IJobCancellationRegistry"/> backed by a dictionary of per-job
/// cancellation sources.
/// </summary>
/// <remarks>
/// Whoever removes an entry under the gate owns disposing it, and only cancelling and releasing
/// remove — which is what keeps a source from ever being observed half-disposed.
/// </remarks>
public sealed partial class JobCancellationRegistry(ILogger<JobCancellationRegistry> logger) : IJobCancellationRegistry, IDisposable
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
            LogCancellationCallbackThrew(_logger, ex, jobId);
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

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "A cancellation callback for job {JobId} threw")]
    private static partial void LogCancellationCallbackThrew(ILogger logger, Exception exception, string jobId);

    #endregion
}
