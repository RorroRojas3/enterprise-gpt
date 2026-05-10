using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RR.AI_Chat.Service.BackgroundJobs
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
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BackgroundJobProcessor> _logger;
        private readonly SemaphoreSlim _gate;
        private readonly int _maxConcurrent;

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundJobProcessor"/> class.
        /// </summary>
        /// <param name="queue">The work-item source.</param>
        /// <param name="store">The snapshot store updated on failure.</param>
        /// <param name="scopeFactory">Used to create a fresh DI scope per job (required for scoped services such as <c>AIChatDbContext</c>).</param>
        /// <param name="configuration">Reads <c>BackgroundJobs:MaxConcurrent</c>; values <c>&lt;= 0</c> resolve to <c>Environment.ProcessorCount * 2</c>.</param>
        /// <param name="logger">Diagnostic logger.</param>
        public BackgroundJobProcessor(
            IBackgroundJobQueue queue,
            IJobStatusStore store,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<BackgroundJobProcessor> logger)
        {
            _queue = queue;
            _store = store;
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

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var item = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
                    await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);

                    var task = ProcessAsync(item, stoppingToken);
                    inFlight[task] = 0;
                    _ = task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown.
            }

            await Task.WhenAll(inFlight.Keys).ConfigureAwait(false);
            _logger.LogInformation("BackgroundJobProcessor stopped");
        }

        private async Task ProcessAsync(JobWorkItem item, CancellationToken cancellationToken)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await item.WorkAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Background job {JobId} cancelled during shutdown", item.JobId);
                _store.Fail(item.JobId, "Job cancelled during host shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background job {JobId} failed", item.JobId);
                _store.Fail(item.JobId, ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            _gate.Dispose();
            base.Dispose();
        }
    }
}
