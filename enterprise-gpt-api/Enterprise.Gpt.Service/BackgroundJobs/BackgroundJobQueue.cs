using System.Threading.Channels;
using Enterprise.Gpt.Service.BackgroundJobs.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.BackgroundJobs;

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

/// <summary>
/// Default <see cref="IBackgroundJobQueue"/> backed by a bounded <see cref="Channel{T}"/> with a single reader
/// (the <see cref="BackgroundJobProcessor"/>) and many writers (request threads).
/// </summary>
/// <remarks>
/// The channel is bounded, and a full channel makes writers wait rather than dropping work. Each queued
/// item holds the whole uploaded file in memory until it is processed, so an unbounded queue would let a
/// burst of large uploads grow without limit and exhaust the host; waiting pushes that backpressure out
/// to the caller's request instead. The capacity is derived from <c>Documents:MaxQueuedBytes</c> divided
/// by the maximum upload size, so the worst-case memory held by the queue is a configured number of
/// bytes rather than an unbounded product of two independent settings.
/// </remarks>
public sealed class BackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<JobWorkItem> _channel;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobQueue"/> class.
    /// </summary>
    /// <param name="options">Supplies the queue's byte budget and the maximum upload size.</param>
    /// <param name="logger">Logger used to record the resolved capacity at startup.</param>
    public BackgroundJobQueue(IOptions<DocumentOptions> options, ILogger<BackgroundJobQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = options.Value.MaxQueueLength;

        _channel = Channel.CreateBounded<JobWorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        logger.LogInformation(
            "Background job queue created with a capacity of {Capacity} item(s), bounding queued uploads to {MaxQueuedBytes} bytes at a maximum upload size of {MaxFileSizeBytes} bytes",
            capacity, options.Value.MaxQueuedBytes, options.Value.MaxFileSizeBytes);
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(JobWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item, nameof(item));
        return _channel.Writer.WriteAsync(item, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<JobWorkItem> DequeueAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }
}
