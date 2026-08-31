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
