using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Service.BackgroundJobs
{
    /// <summary>
    /// Immutable snapshot of a background job's current pipeline state, returned by <see cref="IJobStatusStore.Get"/>.
    /// </summary>
    /// <param name="JobId">The job identifier.</param>
    /// <param name="UserId">
    /// The caller who queued the job. Status reads are checked against this so one user cannot read
    /// another's progress, document id or failure detail.
    /// </param>
    /// <param name="Status">The current pipeline stage.</param>
    /// <param name="Progress">An integer percentage in the range 0..100. Never decreases.</param>
    /// <param name="LastUpdated">UTC timestamp of the most recent transition; used for retention eviction.</param>
    /// <param name="Message">
    /// A short, user-facing description of what the job is doing right now, refreshed even when
    /// <paramref name="Progress"/> cannot move — a remote OCR call is opaque until it returns, so the
    /// message is what tells the user the job is still alive.
    /// </param>
    /// <param name="CompletedUnits">Units of work finished in the current stage, when countable.</param>
    /// <param name="TotalUnits">Total units of work in the current stage, when countable.</param>
    /// <param name="DocumentId">The created document, available once the job has persisted it.</param>
    /// <param name="ErrorMessage">Populated only when <paramref name="Status"/> is <see cref="JobStatus.Failed"/>.</param>
    /// <param name="Target">
    /// What the job is ingesting into, so a cancel can remove whatever it produced without probing both
    /// document tables for the id.
    /// </param>
    public sealed record JobStatusSnapshot(
        string JobId,
        Guid UserId,
        JobStatus Status,
        int Progress,
        DateTimeOffset LastUpdated,
        string? Message = null,
        int? CompletedUnits = null,
        int? TotalUnits = null,
        Guid? DocumentId = null,
        string? ErrorMessage = null,
        JobTarget? Target = null);
}
