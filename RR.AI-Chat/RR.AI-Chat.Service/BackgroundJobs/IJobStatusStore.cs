using RR.AI_Chat.Dto.Enums;

namespace RR.AI_Chat.Service.BackgroundJobs
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
        void Register(string jobId);

        /// <summary>
        /// Updates the snapshot for an in-flight job with a new pipeline stage and progress percentage.
        /// </summary>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="status">The new pipeline stage.</param>
        /// <param name="progress">An integer percentage in the range 0..100.</param>
        void Update(string jobId, JobStatus status, int progress);

        /// <summary>
        /// Marks a job as failed and records the failure message.
        /// </summary>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="errorMessage">A short, user-safe description of the failure.</param>
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
