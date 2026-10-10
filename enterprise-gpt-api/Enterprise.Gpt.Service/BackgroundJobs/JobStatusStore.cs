using System.Collections.Concurrent;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.BackgroundJobs.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.BackgroundJobs;

/// <summary>
/// In-memory store tracking the live status of background jobs. Singleton lifetime; thread-safe.
/// Terminal snapshots (<see cref="JobStatuses.Processed"/>, <see cref="JobStatuses.Failed"/>,
/// <see cref="JobStatuses.Cancelled"/>) are evicted after the configured retention window.
/// </summary>
/// <remarks>
/// The terminal slot is the pipeline's ordering point against a concurrent cancel, so
/// <see cref="Complete"/> and <see cref="Cancel"/> must keep reporting which of them won.
/// </remarks>
public interface IJobStatusStore
{
    /// <summary>
    /// Records a freshly enqueued job in the <see cref="JobStatuses.Queued"/> state with progress 0.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="userId">The caller who queued the job; status reads are checked against it.</param>
    /// <param name="target">What the job is ingesting into, so a cancel can undo it.</param>
    void Register(string jobId, Guid userId, JobTarget target);

    /// <summary>
    /// Updates the snapshot for an in-flight job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="status">The new pipeline stage.</param>
    /// <param name="progress">
    /// An integer percentage in the range 0..100. Clamped upwards against the value already recorded so
    /// a reported percentage never goes backwards, which would read as the job restarting.
    /// </param>
    /// <param name="message">A short, user-facing description of the current step.</param>
    /// <param name="completedUnits">Units of work finished in this stage, when countable.</param>
    /// <param name="totalUnits">Total units of work in this stage, when countable.</param>
    void Report(string jobId, JobStatuses status, int progress, string? message = null, int? completedUnits = null, int? totalUnits = null);

    /// <summary>
    /// Marks a job as successfully completed and records the document it produced.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="documentId">The identifier of the created document.</param>
    /// <param name="message">A short, user-facing completion message.</param>
    /// <returns>
    /// <see langword="false"/> when the job had already reached a terminal state — it was cancelled
    /// while this call was in flight — in which case the caller must undo what it persisted.
    /// </returns>
    bool Complete(string jobId, Guid documentId, string message);

    /// <summary>
    /// Marks a job as cancelled, unless it had already reached a terminal state.
    /// </summary>
    /// <remarks>
    /// Update-only: unlike <see cref="Fail"/> this never creates a snapshot, so a cancel naming an id
    /// that was never registered cannot mint an ownerless entry.
    /// </remarks>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="message">A short, user-facing message describing the cancellation.</param>
    /// <returns>
    /// The snapshot left in place — this call's own <see cref="JobStatuses.Cancelled"/>, or the terminal
    /// one that beat it — or <see langword="null"/> when no snapshot exists.
    /// </returns>
    JobStatusSnapshot? Cancel(string jobId, string message);

    /// <summary>
    /// Marks a job as failed and records the failure message.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="errorMessage">A short, user-safe description of the failure. Must not contain secrets or PII.</param>
    void Fail(string jobId, string errorMessage);

    /// <summary>
    /// Returns the current snapshot for the given job, or <see langword="null"/> if no snapshot exists
    /// (job never registered or already evicted by retention).
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>The latest <see cref="JobStatusSnapshot"/>, or <see langword="null"/>.</returns>
    JobStatusSnapshot? Get(string jobId);
}

/// <summary>
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>-backed <see cref="IJobStatusStore"/>. A periodic timer
/// evicts terminal snapshots older than the configured retention window so memory stays bounded.
/// </summary>
public sealed class JobStatusStore : IJobStatusStore, IDisposable
{
    private readonly ConcurrentDictionary<string, JobStatusSnapshot> _snapshots = new();
    private readonly TimeSpan _retention;
    private readonly Timer _evictionTimer;
    private readonly ILogger<JobStatusStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobStatusStore"/> class.
    /// </summary>
    /// <param name="options">Supplies <see cref="BackgroundJobOptions.RetentionMinutes"/>.</param>
    /// <param name="logger">Logger used for eviction telemetry.</param>
    public JobStatusStore(IOptions<BackgroundJobOptions> options, ILogger<JobStatusStore> logger)
    {
        _logger = logger;
        _retention = TimeSpan.FromMinutes(options.Value.RetentionMinutes);
        _evictionTimer = new Timer(_ => EvictExpired(), state: null, dueTime: _retention, period: _retention);
    }

    /// <inheritdoc />
    public void Register(string jobId, Guid userId, JobTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
        ArgumentNullException.ThrowIfNull(target);
        _snapshots[jobId] = new JobStatusSnapshot(jobId, userId, JobStatuses.Queued, 0, DateTimeOffset.UtcNow, "Waiting to start", Target: target);
    }

    /// <inheritdoc />
    public void Report(string jobId, JobStatuses status, int progress, string? message = null, int? completedUnits = null, int? totalUnits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));

        var clamped = Math.Clamp(progress, 0, 100);

        // AddOrUpdate rather than an indexer assignment: the percentage is derived from the value
        // already stored, and reports for one job can race (the extraction heartbeat fires on a timer
        // while the pipeline advances stages).
        _snapshots.AddOrUpdate(
            jobId,
            // A report for a job that was never registered has no owner to attribute it to. Guid.Empty
            // matches no user, so the snapshot exists for diagnostics but is not readable by anyone.
            _ => new JobStatusSnapshot(jobId, Guid.Empty, status, clamped, DateTimeOffset.UtcNow, message, completedUnits, totalUnits),
            (_, existing) => IsTerminal(existing.Status)
                // A completed or failed job never moves again. Reviving it would also make it
                // permanently un-evictable, since eviction only considers terminal snapshots.
                ? existing
                : existing with
                {
                    Status = status,
                    Progress = Math.Max(existing.Progress, clamped),
                    LastUpdated = DateTimeOffset.UtcNow,
                    Message = message ?? existing.Message,
                    CompletedUnits = completedUnits ?? existing.CompletedUnits,
                    TotalUnits = totalUnits ?? existing.TotalUnits,
                });
    }

    /// <inheritdoc />
    public bool Complete(string jobId, Guid documentId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));

        var installed = _snapshots.AddOrUpdate(
            jobId,
            _ => new JobStatusSnapshot(jobId, Guid.Empty, JobStatuses.Processed, 100, DateTimeOffset.UtcNow, message, DocumentId: documentId),
            // A cancel that got here first owns the outcome, and the document id is deliberately not
            // stamped on it: the caller reads this verdict to learn that it must undo what it wrote,
            // and publishing the id would invite a second party to delete the same rows.
            (_, existing) => IsTerminal(existing.Status)
                ? existing
                : existing with
                {
                    Status = JobStatuses.Processed,
                    Progress = 100,
                    LastUpdated = DateTimeOffset.UtcNow,
                    Message = message,
                    DocumentId = documentId,
                });

        return installed.Status == JobStatuses.Processed && installed.DocumentId == documentId;
    }

    /// <inheritdoc />
    public JobStatusSnapshot? Cancel(string jobId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));

        // A compare-and-swap loop rather than AddOrUpdate, because this must never create: a cancel
        // naming an unknown id would otherwise mint an ownerless snapshot that nobody can read and
        // retention still has to carry.
        while (true)
        {
            if (!_snapshots.TryGetValue(jobId, out var existing))
            {
                return null;
            }

            if (IsTerminal(existing.Status))
            {
                return existing;
            }

            var cancelled = existing with
            {
                Status = JobStatuses.Cancelled,
                LastUpdated = DateTimeOffset.UtcNow,
                Message = message,
            };

            // TryUpdate compares with the default equality comparer, and this is a record — so two
            // snapshots written inside one timestamp tick could compare equal and let the swap succeed
            // against a value it did not read. Harmless: both were non-terminal, so the outcome is the
            // same either way.
            if (_snapshots.TryUpdate(jobId, cancelled, existing))
            {
                return cancelled;
            }
        }
    }

    /// <inheritdoc />
    public void Fail(string jobId, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));

        // Progress is deliberately left where it stopped rather than jumped to 100: a failure is not a
        // completion, and where it stopped tells the user which stage broke.
        _snapshots.AddOrUpdate(
            jobId,
            _ => new JobStatusSnapshot(jobId, Guid.Empty, JobStatuses.Failed, 0, DateTimeOffset.UtcNow, errorMessage, ErrorMessage: errorMessage),
            // Guarded like Report: a job cancelled while it was already unwinding must not have that
            // outcome overwritten by the failure its own cancellation produced.
            (_, existing) => IsTerminal(existing.Status)
                ? existing
                : existing with
                {
                    Status = JobStatuses.Failed,
                    LastUpdated = DateTimeOffset.UtcNow,
                    Message = errorMessage,
                    ErrorMessage = errorMessage,
                });
    }

    /// <inheritdoc />
    public JobStatusSnapshot? Get(string jobId)
    {
        return _snapshots.TryGetValue(jobId, out var snapshot) ? snapshot : null;
    }

    private void EvictExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _retention;
        var removed = 0;

        foreach (var (key, snapshot) in _snapshots)
        {
            if (IsTerminal(snapshot.Status) && snapshot.LastUpdated < cutoff && _snapshots.TryRemove(key, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Evicted {Count} terminal job snapshot(s) older than {RetentionMinutes} minutes", removed, _retention.TotalMinutes);
        }
    }

    private static bool IsTerminal(JobStatuses status) =>
        status is JobStatuses.Processed or JobStatuses.Failed or JobStatuses.Cancelled;

    /// <inheritdoc />
    public void Dispose()
    {
        _evictionTimer.Dispose();
    }
}
