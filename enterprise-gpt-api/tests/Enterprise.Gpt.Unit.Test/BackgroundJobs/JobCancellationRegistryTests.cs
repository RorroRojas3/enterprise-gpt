using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Service.BackgroundJobs;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.BackgroundJobs;

public sealed class JobCancellationRegistryTests : IDisposable
{
    private readonly JobCancellationRegistry _registry = new(NullLogger<JobCancellationRegistry>.Instance);

    public void Dispose() => _registry.Dispose();

    [Fact]
    public void CreateLinkedTokenSource_RegisteredJob_ReturnsALiveSource()
    {
        _registry.Register("job-1");

        using var linked = _registry.CreateLinkedTokenSource("job-1", CancellationToken.None);

        Assert.NotNull(linked);
        Assert.False(linked.IsCancellationRequested);
    }

    [Fact]
    public void CreateLinkedTokenSource_UnregisteredJob_ReturnsNull()
    {
        Assert.Null(_registry.CreateLinkedTokenSource("job-1", CancellationToken.None));
    }

    [Fact]
    public async Task CreateLinkedTokenSource_AfterTheJobWasCancelled_ReturnsNull()
    {
        _registry.Register("job-1");
        await _registry.CancelAsync("job-1");

        // The absence of a registration is the whole record that a queued job was called off; the
        // channel cannot be asked to give the work item back.
        Assert.Null(_registry.CreateLinkedTokenSource("job-1", CancellationToken.None));
    }

    [Fact]
    public async Task CancelAsync_JobAlreadyRunning_TripsTheLinkedToken()
    {
        _registry.Register("job-1");
        using var linked = _registry.CreateLinkedTokenSource("job-1", CancellationToken.None);

        await _registry.CancelAsync("job-1");

        Assert.NotNull(linked);
        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public void CreateLinkedTokenSource_HostStopping_TripsTheLinkedToken()
    {
        _registry.Register("job-1");
        using var stopping = new CancellationTokenSource();

        using var linked = _registry.CreateLinkedTokenSource("job-1", stopping.Token);
        stopping.Cancel();

        Assert.NotNull(linked);
        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelAsync_UnknownJob_ReturnsFalse()
    {
        Assert.False(await _registry.CancelAsync("missing"));
    }

    [Fact]
    public async Task CancelAsync_CalledTwice_OnlyClaimsTheRegistrationOnce()
    {
        _registry.Register("job-1");

        Assert.True(await _registry.CancelAsync("job-1"));
        Assert.False(await _registry.CancelAsync("job-1"));
    }

    [Fact]
    public async Task CancelAsync_AfterRelease_ReturnsFalse()
    {
        _registry.Register("job-1");
        _registry.Release("job-1");

        Assert.False(await _registry.CancelAsync("job-1"));
    }

    [Fact]
    public void Register_SameJobTwice_Throws()
    {
        _registry.Register("job-1");

        Assert.Throws<InvalidOperationException>(() => _registry.Register("job-1"));
    }

    [Fact]
    public async Task Release_AfterAJobEnds_LeavesNothingBehind()
    {
        _registry.Register("job-1");
        _registry.Release("job-1");
        Assert.Equal(0, _registry.ActiveRegistrationCount);

        _registry.Register("job-2");
        await _registry.CancelAsync("job-2");
        Assert.Equal(0, _registry.ActiveRegistrationCount);
    }

    [Fact]
    public void Release_UnknownJob_DoesNothing()
    {
        _registry.Release("missing");

        Assert.Equal(0, _registry.ActiveRegistrationCount);
    }

    [Fact]
    public async Task CancelAsync_RacingReleaseAndLinking_NeverObservesADisposedSource()
    {
        // The failure this guards is an ObjectDisposedException from linking a source another thread
        // disposed a moment earlier, which is why the registry links under its own gate rather than
        // handing tokens out.
        await Parallel.ForAsync(0, 200, TestContext.Current.CancellationToken, async (i, _) =>
        {
            var jobId = $"job-{i}";
            _registry.Register(jobId);

            await Task.WhenAll(
                Task.Run(() => _registry.CreateLinkedTokenSource(jobId, CancellationToken.None)?.Dispose()),
                Task.Run(async () => await _registry.CancelAsync(jobId)),
                Task.Run(() => _registry.Release(jobId)));
        });

        Assert.Equal(0, _registry.ActiveRegistrationCount);
    }
}
