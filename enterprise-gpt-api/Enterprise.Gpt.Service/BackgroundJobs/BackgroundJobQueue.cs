using System.Threading.Channels;

namespace RR.AI_Chat.Service.BackgroundJobs
{
    /// <summary>
    /// Default <see cref="IBackgroundJobQueue"/> backed by an unbounded <see cref="Channel{T}"/> with a single reader
    /// (the <see cref="BackgroundJobProcessor"/>) and many writers (request threads).
    /// </summary>
    public sealed class BackgroundJobQueue : IBackgroundJobQueue
    {
        private readonly Channel<JobWorkItem> _channel = Channel.CreateUnbounded<JobWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

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
