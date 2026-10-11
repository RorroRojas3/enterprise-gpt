using System.Collections.Concurrent;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.BackgroundJobs.Constants;
using Enterprise.Gpt.Service.BackgroundJobs.Exceptions;
using Enterprise.Gpt.Service.BackgroundJobs.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.BackgroundJobs;

/// <summary>
/// Runs queued jobs, each in its own DI scope, at most <see cref="BackgroundJobOptions.MaxConcurrent"/> at once
/// (<c>&lt;= 0</c> means twice the processor count). A throwing job is marked <see cref="JobStatuses.Failed"/>.
/// </summary>
public sealed partial class BackgroundJobProcessor(
    IBackgroundJobQueue queue,
    IJobStatusStore store,
    IJobCancellationRegistry cancellations,
    IServiceScopeFactory scopeFactory,
    IOptions<BackgroundJobOptions> options,
    ILogger<BackgroundJobProcessor> logger) : BackgroundService
{
    private readonly IBackgroundJobQueue _queue = queue;
    private readonly IJobStatusStore _store = store;
    private readonly IJobCancellationRegistry _cancellations = cancellations;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<BackgroundJobProcessor> _logger = logger;
    private readonly int _maxConcurrent = ResolveMaxConcurrent(options.Value.MaxConcurrent);
    private readonly SemaphoreSlim _gate = CreateGate(options.Value.MaxConcurrent);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogProcessorStarted(_logger, _maxConcurrent);

        var inFlight = new ConcurrentDictionary<Task, byte>();

        while (!stoppingToken.IsCancellationRequested)
        {
            JobWorkItem item;
            try
            {
                item = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Dequeued but never started: fail it so the snapshot reaches a terminal, evictable state
                // instead of sticking in Queued.
                LogJobDequeuedButNotStarted(_logger, item.JobId);
                _cancellations.Release(item.JobId);
                _store.Fail(item.JobId, "Host shutdown before job could start.");
                break;
            }

            var task = ProcessAsync(item, stoppingToken);
            inFlight[task] = 0;
            _ = task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
        }

        await Task.WhenAll(inFlight.Keys).ConfigureAwait(false);
        LogProcessorStopped(_logger);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    #region Private methods

    private async Task ProcessAsync(JobWorkItem item, CancellationToken stoppingToken)
    {
        try
        {
            var linked = _cancellations.CreateLinkedTokenSource(item.JobId, stoppingToken);

            if (linked is null)
            {
                // Cancelled while queued (a channel cannot drop an item); the missing registration
                // is the record.
                LogJobCancelledBeforeStart(_logger, item.JobId);
                _store.Cancel(item.JobId, JobMessages.Cancelled);
                return;
            }

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await item.WorkAsync(scope.ServiceProvider, linked.Token).ConfigureAwait(false);
            }
            catch (JobCancelledException)
            {
                // Finished, then lost the terminal slot to a cancel; it already undid its writes, so this is
                // not an unexpected error.
                LogJobCancelledAfterPersisting(_logger, item.JobId);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The work's token is linked to both sources, so only the host token tells a caller
                // cancel from shutdown.
                LogJobCancelledByCaller(_logger, item.JobId);
                _store.Cancel(item.JobId, JobMessages.Cancelled);
            }
            catch (OperationCanceledException)
            {
                LogJobCancelledDuringShutdown(_logger, item.JobId);
                _store.Fail(item.JobId, "Job cancelled during host shutdown.");
            }
            catch (Exception ex)
            {
                LogJobFailed(_logger, ex, item.JobId);
                _store.Fail(item.JobId, DescribeFailure(ex));
            }
            finally
            {
                // Before the outer finally releases the parent; `using var` would reverse the order.
                linked.Dispose();
            }
        }
        finally
        {
            _cancellations.Release(item.JobId);
            _gate.Release();
        }
    }

    private static int ResolveMaxConcurrent(int configured) =>
        configured > 0 ? configured : Environment.ProcessorCount * 2;

    private static SemaphoreSlim CreateGate(int configured)
    {
        var maxConcurrent = ResolveMaxConcurrent(configured);
        return new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    // Validation messages pass through; anything else is replaced, since data-access and SDK messages can
    // embed connection strings and credentialed URLs. The full exception is logged.
    private static string DescribeFailure(Exception exception) => exception switch
    {
        FluentValidation.ValidationException validation => string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)),
        _ => "Processing failed because of an unexpected error. Please try again, or contact support if the problem persists.",
    };

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "BackgroundJobProcessor started (maxConcurrent={MaxConcurrent})")]
    private static partial void LogProcessorStarted(ILogger logger, int maxConcurrent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background job {JobId} dequeued but not started before shutdown")]
    private static partial void LogJobDequeuedButNotStarted(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "BackgroundJobProcessor stopped")]
    private static partial void LogProcessorStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background job {JobId} was cancelled before it started")]
    private static partial void LogJobCancelledBeforeStart(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background job {JobId} was cancelled after it had persisted")]
    private static partial void LogJobCancelledAfterPersisting(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background job {JobId} was cancelled by the caller")]
    private static partial void LogJobCancelledByCaller(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background job {JobId} cancelled during shutdown")]
    private static partial void LogJobCancelledDuringShutdown(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Background job {JobId} failed")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string jobId);

    #endregion
}
