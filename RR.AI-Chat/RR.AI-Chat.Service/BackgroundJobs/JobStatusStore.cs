using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RR.AI_Chat.Dto.Enums;

namespace RR.AI_Chat.Service.BackgroundJobs
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
        public void Register(string jobId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
            _snapshots[jobId] = new JobStatusSnapshot(jobId, JobStatus.Queued, 0, DateTimeOffset.UtcNow);
        }

        /// <inheritdoc />
        public void Update(string jobId, JobStatus status, int progress)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
            _snapshots[jobId] = new JobStatusSnapshot(jobId, status, progress, DateTimeOffset.UtcNow);
        }

        /// <inheritdoc />
        public void Fail(string jobId, string errorMessage)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
            _snapshots[jobId] = new JobStatusSnapshot(jobId, JobStatus.Failed, 100, DateTimeOffset.UtcNow, errorMessage);
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
            status is JobStatus.Processed or JobStatus.Failed;

        /// <inheritdoc />
        public void Dispose()
        {
            _evictionTimer.Dispose();
        }
    }
}
