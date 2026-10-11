using System.Collections.Concurrent;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.BackgroundJobs.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.BackgroundJobs;

/// <summary>
/// Thread-safe, in-memory status of background jobs; terminal snapshots are evicted after the retention window.
/// </summary>
/// <remarks>
/// The terminal slot is the pipeline's ordering point against a concurrent cancel, so
/// <see cref="Complete"/> and <see cref="Cancel"/> must keep reporting which of them won.
/// </remarks>
public interface IJobStatusStore
{
    /// <summary>Records a freshly enqueued job as <see cref="JobStatuses.Queued"/>.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="userId">The caller who queued the job; status reads are checked against it.</param>
    /// <param name="target">What the job is ingesting into, so a cancel can undo it.</param>
    void Register(string jobId, Guid userId, JobTarget target);

    /// <summary>Updates the snapshot for an in-flight job.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="status">The new pipeline stage.</param>
    /// <param name="progress">A 0..100 percentage, never lowered (going backwards reads as a restart).</param>
    /// <param name="message">A short, user-facing description of the current step.</param>
    /// <param name="completedUnits">Units finished in this stage, when countable.</param>
    /// <param name="totalUnits">Total units in this stage, when countable.</param>
    void Report(string jobId, JobStatuses status, int progress, string? message = null, int? completedUnits = null, int? totalUnits = null);

    /// <summary>Marks a job as completed and records the document it produced.</summary>
    /// <returns>
    /// <see langword="false"/> when a cancel already won, in which case the caller must undo what it persisted.
    /// </returns>
    bool Complete(string jobId, Guid documentId, string message);

    /// <summary>Marks a job as cancelled, unless it had already reached a terminal state.</summary>
    /// <remarks>
    /// Update-only, unlike <see cref="Fail"/>, so an unregistered id cannot mint an ownerless entry.
    /// </remarks>
    /// <returns>
    /// The snapshot left in place (this call's <see cref="JobStatuses.Cancelled"/> or the terminal one that
    /// beat it), or <see langword="null"/> when none exists.
    /// </returns>
    JobStatusSnapshot? Cancel(string jobId, string message);

    /// <summary>Marks a job as failed and records the failure message.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="errorMessage">A short, user-safe description. Must not contain secrets or PII.</param>
    void Fail(string jobId, string errorMessage);

    /// <summary>
    /// Returns the current snapshot, or <see langword="null"/> if the job was never registered or was evicted.
    /// </summary>
    JobStatusSnapshot? Get(string jobId);
}

/// <inheritdoc />
public sealed partial class JobStatusStore : IJobStatusStore, IDisposable
{
    private readonly ConcurrentDictionary<string, JobStatusSnapshot> _snapshots = new();
    private readonly TimeSpan _retention;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _evictionTimer;
    private readonly ILogger<JobStatusStore> _logger;

    public JobStatusStore(IOptions<BackgroundJobOptions> options, TimeProvider timeProvider, ILogger<JobStatusStore> logger)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _retention = TimeSpan.FromMinutes(options.Value.RetentionMinutes);
        _evictionTimer = timeProvider.CreateTimer(_ => EvictExpired(), state: null, dueTime: _retention, period: _retention);
    }

    /// <inheritdoc />
    public void Register(string jobId, Guid userId, JobTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
        ArgumentNullException.ThrowIfNull(target);
        _snapshots[jobId] = new JobStatusSnapshot(jobId, userId, JobStatuses.Queued, 0, _timeProvider.GetUtcNow(), "Waiting to start", Target: target);
    }

    /// <inheritdoc />
    public void Report(string jobId, JobStatuses status, int progress, string? message = null, int? completedUnits = null, int? totalUnits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));

        var clamped = Math.Clamp(progress, 0, 100);

        // AddOrUpdate: progress derives from the stored value, and reports race (the extraction heartbeat
        // fires on a timer while the pipeline advances).
        _snapshots.AddOrUpdate(
            jobId,
            // Never registered: Guid.Empty matches no user, so the snapshot is diagnostic only.
            _ => new JobStatusSnapshot(jobId, Guid.Empty, status, clamped, _timeProvider.GetUtcNow(), message, completedUnits, totalUnits),
            (_, existing) => IsTerminal(existing.Status)
                // Terminal snapshots never move; reviving one would also make it un-evictable.
                ? existing
                : existing with
                {
                    Status = status,
                    Progress = Math.Max(existing.Progress, clamped),
                    LastUpdated = _timeProvider.GetUtcNow(),
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
            _ => new JobStatusSnapshot(jobId, Guid.Empty, JobStatuses.Processed, 100, _timeProvider.GetUtcNow(), message, DocumentId: documentId),
            // A cancel that won owns the outcome. The document id is not stamped on it, so only this caller
            // undoes the rows.
            (_, existing) => IsTerminal(existing.Status)
                ? existing
                : existing with
                {
                    Status = JobStatuses.Processed,
                    Progress = 100,
                    LastUpdated = _timeProvider.GetUtcNow(),
                    Message = message,
                    DocumentId = documentId,
                });

        return installed.Status == JobStatuses.Processed && installed.DocumentId == documentId;
    }

    /// <inheritdoc />
    public JobStatusSnapshot? Cancel(string jobId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));

        // A CAS loop rather than AddOrUpdate, because this must never create an ownerless snapshot.
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
                LastUpdated = _timeProvider.GetUtcNow(),
                Message = message,
            };

            // Record equality can match a different snapshot written in the same tick; harmless, since both
            // were non-terminal.
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
            _ => new JobStatusSnapshot(jobId, Guid.Empty, JobStatuses.Failed, 0, _timeProvider.GetUtcNow(), errorMessage, ErrorMessage: errorMessage),
            // Guarded like Report, so a cancel is not overwritten by the failure its own unwinding produced.
            (_, existing) => IsTerminal(existing.Status)
                ? existing
                : existing with
                {
                    Status = JobStatuses.Failed,
                    LastUpdated = _timeProvider.GetUtcNow(),
                    Message = errorMessage,
                    ErrorMessage = errorMessage,
                });
    }

    /// <inheritdoc />
    public JobStatusSnapshot? Get(string jobId)
    {
        return _snapshots.TryGetValue(jobId, out var snapshot) ? snapshot : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _evictionTimer.Dispose();
    }

    #region Private methods

    private void EvictExpired()
    {
        var cutoff = _timeProvider.GetUtcNow() - _retention;
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
            LogEvictedTerminalSnapshots(_logger, removed, _retention.TotalMinutes);
        }
    }

    private static bool IsTerminal(JobStatuses status) =>
        status is JobStatuses.Processed or JobStatuses.Failed or JobStatuses.Cancelled;

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Evicted {Count} terminal job snapshot(s) older than {RetentionMinutes} minutes")]
    private static partial void LogEvictedTerminalSnapshots(ILogger logger, int count, double retentionMinutes);

    #endregion
}
