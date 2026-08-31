using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Service.BackgroundJobs
{
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
        /// <param name="configuration">Reads <c>BackgroundJobs:RetentionMinutes</c> (default 60).</param>
        /// <param name="logger">Logger used for eviction telemetry.</param>
        public JobStatusStore(IConfiguration configuration, ILogger<JobStatusStore> logger)
        {
            _logger = logger;
            var minutes = configuration.GetValue<int?>("BackgroundJobs:RetentionMinutes") ?? 60;
            _retention = TimeSpan.FromMinutes(Math.Max(1, minutes));
            _evictionTimer = new Timer(_ => EvictExpired(), state: null, dueTime: _retention, period: _retention);
        }

        /// <inheritdoc />
        public void Register(string jobId, Guid userId, JobTarget target)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
            ArgumentNullException.ThrowIfNull(target);
            _snapshots[jobId] = new JobStatusSnapshot(jobId, userId, JobStatus.Queued, 0, DateTimeOffset.UtcNow, "Waiting to start", Target: target);
        }

        /// <inheritdoc />
        public void Report(string jobId, JobStatus status, int progress, string? message = null, int? completedUnits = null, int? totalUnits = null)
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
                _ => new JobStatusSnapshot(jobId, Guid.Empty, JobStatus.Processed, 100, DateTimeOffset.UtcNow, message, DocumentId: documentId),
                // A cancel that got here first owns the outcome, and the document id is deliberately not
                // stamped on it: the caller reads this verdict to learn that it must undo what it wrote,
                // and publishing the id would invite a second party to delete the same rows.
                (_, existing) => IsTerminal(existing.Status)
                    ? existing
                    : existing with
                    {
                        Status = JobStatus.Processed,
                        Progress = 100,
                        LastUpdated = DateTimeOffset.UtcNow,
                        Message = message,
                        DocumentId = documentId,
                    });

            return installed.Status == JobStatus.Processed && installed.DocumentId == documentId;
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
                    Status = JobStatus.Cancelled,
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
                _ => new JobStatusSnapshot(jobId, Guid.Empty, JobStatus.Failed, 0, DateTimeOffset.UtcNow, errorMessage, ErrorMessage: errorMessage),
                // Guarded like Report: a job cancelled while it was already unwinding must not have that
                // outcome overwritten by the failure its own cancellation produced.
                (_, existing) => IsTerminal(existing.Status)
                    ? existing
                    : existing with
                    {
                        Status = JobStatus.Failed,
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

        private static bool IsTerminal(JobStatus status) =>
            status is JobStatus.Processed or JobStatus.Failed or JobStatus.Cancelled;

        /// <inheritdoc />
        public void Dispose()
        {
            _evictionTimer.Dispose();
        }
    }
}
