using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Service.BackgroundJobs
{
    /// <summary>
    /// In-memory store tracking the live status of background jobs. Singleton lifetime; thread-safe.
    /// Terminal snapshots (<see cref="JobStatus.Processed"/>, <see cref="JobStatus.Failed"/>) are evicted
    /// after the configured retention window.
    /// </summary>
    public interface IJobStatusStore
    {
        /// <summary>
        /// Records a freshly enqueued job in the <see cref="JobStatus.Queued"/> state with progress 0.
        /// </summary>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="userId">The caller who queued the job; status reads are checked against it.</param>
        void Register(string jobId, Guid userId);

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
        void Report(string jobId, JobStatus status, int progress, string? message = null, int? completedUnits = null, int? totalUnits = null);

        /// <summary>
        /// Marks a job as successfully completed and records the document it produced.
        /// </summary>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="documentId">The identifier of the created document.</param>
        /// <param name="message">A short, user-facing completion message.</param>
        void Complete(string jobId, Guid documentId, string message);

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
}
