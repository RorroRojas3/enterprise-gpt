namespace Enterprise.Gpt.Service.BackgroundJobs
{
    /// <summary>
    /// Represents a unit of background work submitted to the <see cref="IBackgroundJobQueue"/>.
    /// </summary>
    /// <param name="JobId">The identifier used by callers to query status via <see cref="IJobStatusStore"/>.</param>
    /// <param name="WorkAsync">The delegate invoked by the processor with a per-job DI scope and the host stopping token.</param>
    public sealed record JobWorkItem(
        string JobId,
        Func<IServiceProvider, CancellationToken, Task> WorkAsync);
}
