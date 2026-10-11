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
    /// <summary>Enqueues work, waiting while the queue is full.</summary>
    /// <remarks>The token cancels only the enqueue; the work runs under the processor's token.</remarks>
    ValueTask EnqueueAsync(JobWorkItem item, CancellationToken cancellationToken);

    /// <summary>Awaits the next work item.</summary>
    ValueTask<JobWorkItem> DequeueAsync(CancellationToken cancellationToken);
}

/// <summary>A bounded single-reader <see cref="Channel{T}"/>; writers wait when it is full.</summary>
/// <remarks>
/// Each item holds a whole upload in memory, so capacity is <c>Documents:MaxQueuedBytes</c> over the maximum
/// upload size, and backpressure falls on the caller's request.
/// </remarks>
public sealed partial class BackgroundJobQueue(IOptions<DocumentOptions> options, ILogger<BackgroundJobQueue> logger)
    : IBackgroundJobQueue
{
    private readonly Channel<JobWorkItem> _channel = CreateChannel(options, logger);

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

    #region Private methods

    private static Channel<JobWorkItem> CreateChannel(IOptions<DocumentOptions> options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = options.Value.MaxQueueLength;
        var channel = Channel.CreateBounded<JobWorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        LogQueueCreated(logger, capacity, options.Value.MaxQueuedBytes, options.Value.MaxFileSizeBytes);
        return channel;
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Background job queue created with a capacity of {Capacity} item(s), bounding queued uploads to {MaxQueuedBytes} bytes at a maximum upload size of {MaxFileSizeBytes} bytes")]
    private static partial void LogQueueCreated(
        ILogger logger, int capacity, long maxQueuedBytes, long maxFileSizeBytes);

    #endregion
}
