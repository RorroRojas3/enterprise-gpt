using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.BackgroundJobs;

/// <summary>
/// Drives the real processor over the real queue, store and registry — nothing here is substituted,
/// because what is under test is how the three agree on a job outcome.
/// </summary>
public sealed class BackgroundJobProcessorTests : IDisposable
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly JobTarget Target = new(JobTargetKind.Conversation, Guid.NewGuid());

    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundJobs:MaxConcurrent"] = "1" })
        .Build();

    private readonly BackgroundJobQueue _queue = new(
        Options.Create(new DocumentOptions { MaxFileSizeBytes = 1024, MaxQueuedBytes = 8192 }),
        NullLogger<BackgroundJobQueue>.Instance);

    private readonly JobStatusStore _store;
    private readonly JobCancellationRegistry _registry = new(NullLogger<JobCancellationRegistry>.Instance);
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly BackgroundJobProcessor _processor;

    public BackgroundJobProcessorTests()
    {
        _store = new JobStatusStore(_configuration, NullLogger<JobStatusStore>.Instance);
        _processor = new BackgroundJobProcessor(
            _queue,
            _store,
            _registry,
            _services.GetRequiredService<IServiceScopeFactory>(),
            _configuration,
            NullLogger<BackgroundJobProcessor>.Instance);
    }

    public void Dispose()
    {
        _processor.Dispose();
        _store.Dispose();
        _registry.Dispose();
        _services.Dispose();
    }

    private async Task<string> EnqueueAsync(Func<CancellationToken, Task> work)
    {
        var jobId = Guid.NewGuid().ToString();
        _store.Register(jobId, OwnerId, Target);
        _registry.Register(jobId);
        await _queue.EnqueueAsync(new JobWorkItem(jobId, (_, token) => work(token)), TestContext.Current.CancellationToken);

        return jobId;
    }

    /// <summary>Waits for a snapshot to reach a terminal state rather than sleeping a fixed span.</summary>
    private async Task<JobStatusSnapshot> WaitForTerminalAsync(string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = _store.Get(jobId);
            if (snapshot is not null && snapshot.Status is JobStatus.Processed or JobStatus.Failed or JobStatus.Cancelled)
            {
                return snapshot;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Job {jobId} did not reach a terminal state.");
    }

    [Fact]
    public async Task ProcessAsync_JobCancelledWhileRunning_ReportsCancelledRatherThanFailed()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var jobId = await EnqueueAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });

        await _processor.StartAsync(TestContext.Current.CancellationToken);
        await started.Task;

        await _registry.CancelAsync(jobId);

        // The regression this pins: the token the work observed is linked to the host token too, so a
        // filter testing "was I cancelled" is true for both causes and would report a shutdown.
        var snapshot = await WaitForTerminalAsync(jobId);
        Assert.Equal(JobStatus.Cancelled, snapshot.Status);

        await _processor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _registry.ActiveRegistrationCount);
    }

    [Fact]
    public async Task ProcessAsync_JobCancelledBeforeItStarted_NeverRunsTheWork()
    {
        var ran = false;

        // MaxConcurrent is 1, so this one holds the only slot and the second stays in the channel.
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(async _ =>
        {
            blockerStarted.SetResult();
            await blocker.Task;
        });

        var queued = await EnqueueAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await _processor.StartAsync(TestContext.Current.CancellationToken);
        await blockerStarted.Task;

        await _registry.CancelAsync(queued);
        blocker.SetResult();

        var snapshot = await WaitForTerminalAsync(queued);

        Assert.Equal(JobStatus.Cancelled, snapshot.Status);
        // The channel cannot be asked for the item back, so the absence of a registration is what
        // stops it — no DI scope, no work.
        Assert.False(ran);

        await _processor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProcessAsync_JobThatLostTheTerminalSlot_IsNotReportedAsAFailure()
    {
        var jobId = await EnqueueAsync(_ => throw new JobCancelledException("Upload job was cancelled."));

        // The job already undid its own work, so the outcome the cancel published must stand.
        _store.Cancel(jobId, "Cancelled");

        await _processor.StartAsync(TestContext.Current.CancellationToken);

        var snapshot = await WaitForTerminalAsync(jobId);
        Assert.Equal(JobStatus.Cancelled, snapshot.Status);
        Assert.Null(snapshot.ErrorMessage);

        await _processor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProcessAsync_JobThatThrows_StillReportsAFailure()
    {
        var jobId = await EnqueueAsync(_ => throw new InvalidOperationException("boom"));

        await _processor.StartAsync(TestContext.Current.CancellationToken);

        var snapshot = await WaitForTerminalAsync(jobId);
        Assert.Equal(JobStatus.Failed, snapshot.Status);
        // Sanitized: the raw message could carry a connection string.
        Assert.DoesNotContain("boom", snapshot.ErrorMessage);

        await _processor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _registry.ActiveRegistrationCount);
    }

    [Fact]
    public async Task ProcessAsync_JobThatSucceeds_ReleasesItsRegistration()
    {
        string? jobId = null;
        // The work publishes its own terminal state, exactly as the ingestion pipeline does.
        jobId = await EnqueueAsync(_ =>
        {
            _store.Complete(jobId!, Guid.NewGuid(), "Ready");
            return Task.CompletedTask;
        });

        await _processor.StartAsync(TestContext.Current.CancellationToken);
        await WaitForTerminalAsync(jobId);

        await _processor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _registry.ActiveRegistrationCount);
    }
}
