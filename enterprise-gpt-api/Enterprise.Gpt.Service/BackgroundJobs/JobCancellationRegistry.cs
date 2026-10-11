using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.BackgroundJobs;

/// <summary>Tracks each queued or running job's cancellation source so a caller can cancel it.</summary>
/// <remarks>
/// Process-local, like the queue and status store. See <c>docs/documents/ingestion.md</c> before distributing
/// any of the three.
/// </remarks>
public interface IJobCancellationRegistry
{
    /// <summary>Records a cancellation source for a job that is being enqueued.</summary>
    /// <remarks>
    /// At enqueue, so a job cancelled while queued, or while a full queue blocks the caller, has a record.
    /// </remarks>
    void Register(string jobId);

    /// <summary>
    /// Creates the token a job runs under, linking its own cancellation source to the host stopping token.
    /// </summary>
    /// <remarks>Linked inside the gate so a concurrent cancel cannot dispose the source in between.</remarks>
    /// <returns>
    /// The linked source, which the caller owns and must dispose, or <see langword="null"/> when the
    /// job was cancelled before it started.
    /// </returns>
    CancellationTokenSource? CreateLinkedTokenSource(string jobId, CancellationToken stoppingToken);

    /// <summary>Requests cancellation of a queued or running job.</summary>
    /// <returns><see langword="true"/> when a live registration was claimed and signalled.</returns>
    Task<bool> CancelAsync(string jobId);

    /// <summary>Drops a finished job registration.</summary>
    void Release(string jobId);
}

/// <inheritdoc />
/// <remarks>
/// Whoever removes an entry under the gate disposes it, and only cancel and release remove, so a source is
/// never observed half-disposed.
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
            // Registered at enqueue and released at job end, so a missing entry means a cancel claimed it.
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
            // Outside the gate: cancelling runs pipeline callbacks (HTTP aborts, command cancellation) that
            // must not hold up every other cancel.
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
