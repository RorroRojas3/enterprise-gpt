using Enterprise.Gpt.Common.Enums;


namespace Enterprise.Gpt.Service.BackgroundJobs.Models;

/// <summary>
/// Immutable snapshot of a background job's current pipeline state, returned by <see cref="IJobStatusStore.Get"/>.
/// </summary>
/// <param name="JobId">The job id.</param>
/// <param name="UserId">The caller who queued the job; status reads are checked against it.</param>
/// <param name="Status">The current pipeline stage.</param>
/// <param name="Progress">An integer percentage in the range 0..100. Never decreases.</param>
/// <param name="LastUpdated">UTC timestamp of the most recent transition; used for retention eviction.</param>
/// <param name="Message">
/// The user-facing current step, refreshed even when <paramref name="Progress"/> cannot move (an opaque
/// OCR call) so the job still shows signs of life.
/// </param>
/// <param name="CompletedUnits">Units of work finished in the current stage, when countable.</param>
/// <param name="TotalUnits">Total units of work in the current stage, when countable.</param>
/// <param name="DocumentId">The created document, available once the job has persisted it.</param>
/// <param name="ErrorMessage">Populated only when <paramref name="Status"/> is <see cref="JobStatuses.Failed"/>.</param>
/// <param name="Target">What the job ingests into, so a cancel need not probe both document tables.</param>
public sealed record JobStatusSnapshot(
    string JobId,
    Guid UserId,
    JobStatuses Status,
    int Progress,
    DateTimeOffset LastUpdated,
    string? Message = null,
    int? CompletedUnits = null,
    int? TotalUnits = null,
    Guid? DocumentId = null,
    string? ErrorMessage = null,
    JobTarget? Target = null);
