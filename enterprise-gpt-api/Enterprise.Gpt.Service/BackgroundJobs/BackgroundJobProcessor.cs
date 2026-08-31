using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.BackgroundJobs
{
    /// <summary>
    /// Hosted service that consumes <see cref="JobWorkItem"/> entries from the <see cref="IBackgroundJobQueue"/>,
    /// executes each in its own DI scope, and throttles concurrency with a <see cref="SemaphoreSlim"/>.
    /// On exception the failing job's snapshot is updated to <see cref="Dto.Enums.JobStatus.Failed"/>; the loop continues.
    /// </summary>
    public sealed class BackgroundJobProcessor : BackgroundService
    {
        private readonly IBackgroundJobQueue _queue;
        private readonly IJobStatusStore _store;
        private readonly IJobCancellationRegistry _cancellations;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BackgroundJobProcessor> _logger;
        private readonly SemaphoreSlim _gate;
        private readonly int _maxConcurrent;

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundJobProcessor"/> class.
        /// </summary>
        /// <param name="queue">The work-item source.</param>
        /// <param name="store">The snapshot store updated on failure.</param>
        /// <param name="cancellations">Supplies each job token and is released when the job ends.</param>
        /// <param name="scopeFactory">Used to create a fresh DI scope per job (required for scoped services such as <c>EnterpriseGptDbContext</c>).</param>
        /// <param name="configuration">Reads <c>BackgroundJobs:MaxConcurrent</c>; values <c>&lt;= 0</c> resolve to <c>Environment.ProcessorCount * 2</c>.</param>
        /// <param name="logger">Diagnostic logger.</param>
        public BackgroundJobProcessor(
            IBackgroundJobQueue queue,
            IJobStatusStore store,
            IJobCancellationRegistry cancellations,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<BackgroundJobProcessor> logger)
        {
            _queue = queue;
            _store = store;
            _cancellations = cancellations;
            _scopeFactory = scopeFactory;
            _logger = logger;

            var configured = configuration.GetValue<int?>("BackgroundJobs:MaxConcurrent") ?? 0;
            _maxConcurrent = configured > 0 ? configured : Environment.ProcessorCount * 2;
            _gate = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BackgroundJobProcessor started (maxConcurrent={MaxConcurrent})", _maxConcurrent);

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
                    _logger.LogWarning("Background job {JobId} dequeued but not started before shutdown", item.JobId);
                    _cancellations.Release(item.JobId);
                    _store.Fail(item.JobId, "Host shutdown before job could start.");
                    break;
                }

                var task = ProcessAsync(item, stoppingToken);
                inFlight[task] = 0;
                _ = task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
            }

            await Task.WhenAll(inFlight.Keys).ConfigureAwait(false);
            _logger.LogInformation("BackgroundJobProcessor stopped");
        }

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
                    _logger.LogInformation("Background job {JobId} was cancelled before it started", item.JobId);
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
                    _logger.LogInformation("Background job {JobId} was cancelled after it had persisted", item.JobId);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Which source fired is the only thing separating these two, and the token the work
                    // observed is linked to both — so the test has to be on the host token, not on the
                    // one that threw.
                    _logger.LogInformation("Background job {JobId} was cancelled by the caller", item.JobId);
                    _store.Cancel(item.JobId, JobMessages.Cancelled);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Background job {JobId} cancelled during shutdown", item.JobId);
                    _store.Fail(item.JobId, "Job cancelled during host shutdown.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background job {JobId} failed", item.JobId);
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

        /// <inheritdoc />
        public override void Dispose()
        {
            _gate.Dispose();
            base.Dispose();
        }
    }
}
