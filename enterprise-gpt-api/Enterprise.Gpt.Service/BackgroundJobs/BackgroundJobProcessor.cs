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
/// Hosted service that consumes <see cref="JobWorkItem"/> entries from the <see cref="IBackgroundJobQueue"/>,
/// executes each in its own DI scope, and throttles concurrency with a <see cref="SemaphoreSlim"/>.
/// On exception the failing job's snapshot is updated to <see cref="JobStatuses.Failed"/>; the loop continues.
/// </summary>
/// <param name="queue">The work-item source.</param>
/// <param name="store">The snapshot store updated on failure.</param>
/// <param name="cancellations">Supplies each job token and is released when the job ends.</param>
/// <param name="scopeFactory">Used to create a fresh DI scope per job (required for scoped services such as <c>EnterpriseGptDbContext</c>).</param>
/// <param name="options">Supplies <see cref="BackgroundJobOptions.MaxConcurrent"/>; values <c>&lt;= 0</c> resolve to <c>Environment.ProcessorCount * 2</c>.</param>
/// <param name="logger">Diagnostic logger.</param>
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
                // Item was dequeued but the host is shutting down before we could acquire a
                // concurrency slot. The work item would otherwise be silently dropped, leaving
                // the snapshot stuck in Queued. Mark it Failed so the client sees a terminal
                // state and the snapshot becomes evictable.
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
                // Cancelled while it sat in the queue, which the channel cannot be asked to forget.
                // The registration is gone, and its absence is the record — so nothing runs and no
                // scope is created.
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
                // The job finished its work and then lost the terminal slot to a cancel. It has
                // already undone what it persisted, and DescribeFailure must not turn that into an
                // unexpected error.
                LogJobCancelledAfterPersisting(_logger, item.JobId);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Which source fired is the only thing separating these two, and the token the work
                // observed is linked to both — so the test has to be on the host token, not on the
                // one that threw.
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
                // The child before the parent the outer finally releases. `using var` would run in
                // the opposite order.
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

    /// <summary>
    /// Produces a message safe to hand back to the caller.
    /// </summary>
    /// <remarks>
    /// Validation messages are written for the user and say something actionable, so they are passed
    /// through. Everything else is replaced: exception messages from data access and Azure SDK clients
    /// routinely embed connection strings, endpoints and request URLs with credentials in them, and the
    /// job status endpoint is not an appropriate place for any of that. The full exception is logged.
    /// </remarks>
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
