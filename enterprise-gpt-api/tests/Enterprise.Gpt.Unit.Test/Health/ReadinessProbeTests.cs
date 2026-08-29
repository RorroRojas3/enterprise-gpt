using Enterprise.Gpt.Api.Health;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Health;

/// <summary>
/// Covers the cache that keeps an anonymous readiness route from probing SQL and Cosmos once per caller.
/// </summary>
public sealed class ReadinessProbeTests
{
    [Fact]
    public async Task CheckAsync_TwoCallsInsideTheWindow_RunsTheChecksOnce()
    {
        var check = new CountingHealthCheck();
        var probe = Build(check, out _);

        await probe.CheckAsync(_ => true, TestContext.Current.CancellationToken);
        await probe.CheckAsync(_ => true, TestContext.Current.CancellationToken);

        Assert.Equal(1, check.Calls);
    }

    [Fact]
    public async Task CheckAsync_AfterTheWindow_RunsTheChecksAgain()
    {
        var check = new CountingHealthCheck();
        var probe = Build(check, out var clock);

        await probe.CheckAsync(_ => true, TestContext.Current.CancellationToken);
        clock.Advance(ReadinessProbe.Ttl + TimeSpan.FromMilliseconds(1));
        await probe.CheckAsync(_ => true, TestContext.Current.CancellationToken);

        Assert.Equal(2, check.Calls);
    }

    /// <summary>
    /// The window is what bounds the cost of the route; a burst arriving on a cold entry must collapse
    /// onto one probe rather than one each.
    /// </summary>
    [Fact]
    public async Task CheckAsync_ABurstOnAColdEntry_RunsTheChecksOnce()
    {
        var check = new CountingHealthCheck { Gate = new TaskCompletionSource() };
        var probe = Build(check, out _);

        var callers = Enumerable.Range(0, 8)
            .Select(_ => probe.CheckAsync(_ => true, TestContext.Current.CancellationToken))
            .ToArray();

        check.Gate.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, check.Calls);
    }

    /// <summary>
    /// The probe is shared, so a caller that walks away — or a platform whose own timeout is shorter
    /// than the registrations' — must not cancel the work everyone else is queued on, which would
    /// leave the cache cold under exactly the conditions it exists for.
    /// </summary>
    [Fact]
    public async Task CheckAsync_TheCallerCancelsMidProbe_StillFillsTheCache()
    {
        var check = new CountingHealthCheck { ObserveToken = true, Gate = new TaskCompletionSource() };
        var probe = Build(check, out _);

        using var cancellation = new CancellationTokenSource();
        var first = probe.CheckAsync(_ => true, cancellation.Token);
        await cancellation.CancelAsync();
        check.Gate!.SetResult();

        await first;
        await probe.CheckAsync(_ => true, TestContext.Current.CancellationToken);

        Assert.Equal(1, check.Calls);
    }

    [Fact]
    public async Task CheckAsync_AFailingCheck_ReportsUnhealthyRatherThanThrowing()
    {
        var check = new CountingHealthCheck { Result = HealthCheckResult.Unhealthy() };
        var probe = Build(check, out _);

        var report = await probe.CheckAsync(_ => true, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }

    private static ReadinessProbe Build(IHealthCheck check, out MutableTimeProvider clock)
    {
        clock = new MutableTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddCheck("probe", check);

        var health = services.BuildServiceProvider().GetRequiredService<HealthCheckService>();

        return new ReadinessProbe(health, clock, NullLogger<ReadinessProbe>.Instance);
    }

    private sealed class CountingHealthCheck : IHealthCheck
    {
        private int _calls;

        public int Calls => _calls;

        public HealthCheckResult Result { get; init; } = HealthCheckResult.Healthy();

        /// <summary>Held open to keep the first probe in flight while a burst queues behind it.</summary>
        public TaskCompletionSource? Gate { get; init; }

        /// <summary>Whether the check honours the token it is handed, as a real check would.</summary>
        public bool ObserveToken { get; init; }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (ObserveToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Result;
        }
    }
}
