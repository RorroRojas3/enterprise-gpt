using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Service.Settings;
using System.Threading.Channels;

namespace Enterprise.Gpt.Service.BackgroundJobs
{
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
}
