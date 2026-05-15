using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Service.BackgroundJobs
{
    /// <summary>
    /// Immutable snapshot of a background job's current pipeline state, returned by <see cref="IJobStatusStore.Get"/>.
    /// </summary>
    /// <param name="JobId">The job identifier.</param>
    /// <param name="Status">The current pipeline stage (Queued, Uploading, Extracting, Embedding, Processed, Failed).</param>
    /// <param name="Progress">An integer percentage in the range 0..100.</param>
    /// <param name="LastUpdated">UTC timestamp of the most recent transition; used for retention eviction.</param>
    /// <param name="ErrorMessage">Populated only when <paramref name="Status"/> is <see cref="JobStatus.Failed"/>.</param>
    public sealed record JobStatusSnapshot(
        string JobId,
        JobStatus Status,
        int Progress,
        DateTimeOffset LastUpdated,
        string? ErrorMessage = null);
}
