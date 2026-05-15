namespace Enterprise.Gpt.Service.BackgroundJobs
{
    /// <summary>
    /// FIFO queue used to hand off background work from request threads to the <see cref="BackgroundJobProcessor"/>.
    /// </summary>
    public interface IBackgroundJobQueue
    {
        /// <summary>
        /// Enqueues a unit of work for asynchronous processing.
        /// </summary>
        /// <param name="item">The work item to enqueue.</param>
        /// <param name="cancellationToken">Cancels the enqueue operation only; the work itself runs under the processor's stopping token.</param>
        /// <returns>A task that completes when the item is accepted by the queue.</returns>
        ValueTask EnqueueAsync(JobWorkItem item, CancellationToken cancellationToken);

        /// <summary>
        /// Awaits and returns the next work item from the queue.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait when the host stops.</param>
        /// <returns>The next available <see cref="JobWorkItem"/>.</returns>
        ValueTask<JobWorkItem> DequeueAsync(CancellationToken cancellationToken);
    }
}
