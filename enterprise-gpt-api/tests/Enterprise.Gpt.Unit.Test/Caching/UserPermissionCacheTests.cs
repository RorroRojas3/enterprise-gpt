using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Caching;

public sealed class UserPermissionCacheTests
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);

    private static UserPermissionCache CreateCache(
        MutableTimeProvider? clock = null, TimeSpan? entryLifetime = null)
    {
        return new UserPermissionCache(
            NullLogger<UserPermissionCache>.Instance,
            Options.Create(new UserPermissionCacheOptions { EntryLifetime = entryLifetime ?? _lifetime }),
            clock ?? new MutableTimeProvider());
    }

    private static Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> Loader(params Guid[] permissionIds)
    {
        return _ => Task.FromResult<IReadOnlyCollection<Guid>>(permissionIds);
    }

    private static Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> ThrowingLoader()
    {
        return _ => throw new InvalidOperationException("The loader must not run on a cache hit.");
    }

    [Fact]
    public async Task GetOrLoadAsync_ConcurrentCallersSameUser_RunSingleLoaderAndShareTheSet()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> loader = async ct =>
        {
            Interlocked.Increment(ref invocations);
            return await tcs.Task.WaitAsync(ct);
        };

        var first = cache.GetOrLoadAsync(userId, loader, TestContext.Current.CancellationToken);
        var second = cache.GetOrLoadAsync(userId, loader, TestContext.Current.CancellationToken);
        tcs.SetResult([permissionId]);

        var firstSet = await first;
        var secondSet = await second;

        Assert.Equal(1, invocations);
        Assert.Same(firstSet, secondSet);
        Assert.Contains(permissionId, firstSet);
    }

    [Fact]
    public async Task GetOrLoadAsync_SecondCall_DoesNotRunTheLoader()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();

        await cache.GetOrLoadAsync(userId, Loader(permissionId), TestContext.Current.CancellationToken);
        var second = await cache.GetOrLoadAsync(userId, ThrowingLoader(), TestContext.Current.CancellationToken);

        Assert.Contains(permissionId, second);
    }

    [Fact]
    public async Task GetOrLoadAsync_DifferentUsers_RunSeparateLoaders()
    {
        var cache = CreateCache();
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var firstPermissionId = Guid.NewGuid();
        var secondPermissionId = Guid.NewGuid();

        var first = await cache.GetOrLoadAsync(firstUserId, Loader(firstPermissionId), TestContext.Current.CancellationToken);
        var second = await cache.GetOrLoadAsync(secondUserId, Loader(secondPermissionId), TestContext.Current.CancellationToken);

        Assert.Contains(firstPermissionId, first);
        Assert.DoesNotContain(secondPermissionId, first);
        Assert.Contains(secondPermissionId, second);
    }

    [Fact]
    public async Task GetOrLoadAsync_UserWithNoGrants_CachesTheEmptySetInsteadOfReloading()
    {
        // Treating an empty set as a miss would make every 403 a database round trip — the exact
        // cost this cache exists to remove for non-admins hitting admin routes.
        var cache = CreateCache();
        var userId = Guid.NewGuid();

        var first = await cache.GetOrLoadAsync(userId, Loader(), TestContext.Current.CancellationToken);
        var second = await cache.GetOrLoadAsync(userId, ThrowingLoader(), TestContext.Current.CancellationToken);

        Assert.Empty(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task GetOrLoadAsync_LoaderFails_PropagatesAndNextCallRunsAFreshLoader()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrLoadAsync(
            userId,
            _ => Task.FromException<IReadOnlyCollection<Guid>>(new InvalidOperationException("boom")),
            TestContext.Current.CancellationToken));

        var recovered = await cache.GetOrLoadAsync(userId, Loader(permissionId), TestContext.Current.CancellationToken);

        Assert.Contains(permissionId, recovered);
    }

    [Fact]
    public async Task GetOrLoadAsync_LoadingCallerCancels_NextCallerLoadsAfresh()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        using var loadingCts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loading = cache.GetOrLoadAsync(
            userId,
            async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            },
            loadingCts.Token);

        await started.Task;
        await loadingCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);

        var recovered = await cache.GetOrLoadAsync(userId, Loader(permissionId), TestContext.Current.CancellationToken);

        Assert.Contains(permissionId, recovered);
    }

    [Fact]
    public async Task GetOrLoadAsync_LoadingCallerCancels_AttachedCoWaiterRetriesWithItsOwnLoader()
    {
        // The co-waiter attaches while the load is in flight, so when the creating request aborts
        // the co-waiter observes a task it did not start failing — the branch that must retry rather
        // than surface a failure the co-waiter did not cause.
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        using var loadingCts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loading = cache.GetOrLoadAsync(
            userId,
            async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            },
            loadingCts.Token);

        await started.Task;
        var coWaiter = cache.GetOrLoadAsync(userId, Loader(permissionId), TestContext.Current.CancellationToken);

        await loadingCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);

        Assert.Contains(permissionId, await coWaiter);
    }

    [Fact]
    public async Task GetOrLoadAsync_AttachedCoWaiter_RetriesWhenTheCreatorsLoaderFaults()
    {
        // A creating request whose DbContext scope is disposed out from under an abandoned load
        // faults with something other than OperationCanceledException. The co-waiter must not 500.
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var gate = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var creating = cache.GetOrLoadAsync(
            userId,
            async _ =>
            {
                started.TrySetResult();
                return await gate.Task;
            },
            TestContext.Current.CancellationToken);

        await started.Task;
        var coWaiter = cache.GetOrLoadAsync(userId, Loader(permissionId), TestContext.Current.CancellationToken);

        gate.SetException(new ObjectDisposedException(nameof(IServiceProvider)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => creating);

        Assert.Contains(permissionId, await coWaiter);
    }

    [Fact]
    public async Task GetOrLoadAsync_CoWaiterCancels_LeavesTheInFlightLoadForOthers()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> loader = async ct =>
        {
            Interlocked.Increment(ref invocations);
            return await tcs.Task.WaitAsync(ct);
        };

        var loading = cache.GetOrLoadAsync(userId, loader, TestContext.Current.CancellationToken);
        using var coWaiterCts = new CancellationTokenSource();
        var coWaiter = cache.GetOrLoadAsync(userId, loader, coWaiterCts.Token);

        await coWaiterCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coWaiter);

        tcs.SetResult([permissionId]);
        await loading;

        // The abandoned waiter must not have evicted the load still running for everyone else.
        var third = await cache.GetOrLoadAsync(userId, ThrowingLoader(), TestContext.Current.CancellationToken);

        Assert.Equal(1, invocations);
        Assert.Contains(permissionId, third);
    }

    [Fact]
    public async Task GetOrLoadAsync_EntryOlderThanTheLifetime_ReloadsAndReplaces()
    {
        var clock = new MutableTimeProvider();
        var cache = CreateCache(clock);
        var userId = Guid.NewGuid();
        var stalePermissionId = Guid.NewGuid();
        var freshPermissionId = Guid.NewGuid();

        await cache.GetOrLoadAsync(userId, Loader(stalePermissionId), TestContext.Current.CancellationToken);
        clock.Advance(_lifetime + TimeSpan.FromSeconds(1));
        var reloaded = await cache.GetOrLoadAsync(userId, Loader(freshPermissionId), TestContext.Current.CancellationToken);

        Assert.Contains(freshPermissionId, reloaded);
        Assert.DoesNotContain(stalePermissionId, reloaded);
    }

    [Fact]
    public async Task GetOrLoadAsync_EntryExactlyAtTheLifetimeBoundary_Reloads()
    {
        var clock = new MutableTimeProvider();
        var cache = CreateCache(clock);
        var userId = Guid.NewGuid();
        var freshPermissionId = Guid.NewGuid();

        await cache.GetOrLoadAsync(userId, Loader(Guid.NewGuid()), TestContext.Current.CancellationToken);
        clock.Advance(_lifetime);
        var reloaded = await cache.GetOrLoadAsync(userId, Loader(freshPermissionId), TestContext.Current.CancellationToken);

        Assert.Contains(freshPermissionId, reloaded);
    }

    [Fact]
    public async Task GetOrLoadAsync_FreshlyLoadedEntry_IsServedEvenWithAZeroWidthWindow()
    {
        // A freshly loaded entry is always served, so a pathologically short lifetime degrades to
        // "load every request" rather than looping until MaxAttempts and throwing.
        var clock = new MutableTimeProvider();
        var cache = CreateCache(clock, TimeSpan.FromTicks(1));
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();

        var loaded = await cache.GetOrLoadAsync(userId, Loader(permissionId), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        var reloaded = await cache.GetOrLoadAsync(userId, Loader(permissionId), TestContext.Current.CancellationToken);

        Assert.Contains(permissionId, loaded);
        Assert.Contains(permissionId, reloaded);
    }

    [Fact]
    public async Task GetOrLoadAsync_LoaderCollectionMutatedAfterwards_DoesNotAffectTheCachedSet()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var mutable = new List<Guid> { permissionId };

        var loaded = await cache.GetOrLoadAsync(
            userId, _ => Task.FromResult<IReadOnlyCollection<Guid>>(mutable), TestContext.Current.CancellationToken);
        mutable.Clear();
        mutable.Add(Guid.NewGuid());

        Assert.Contains(permissionId, loaded);
        Assert.Single(loaded);
    }

    [Fact]
    public async Task Set_ThenGetOrLoadAsync_ReturnsTheSuppliedSetWithoutRunningTheLoader()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();

        cache.Set(userId, cache.CaptureInvalidationStamp(), [permissionId]);
        var cached = await cache.GetOrLoadAsync(userId, ThrowingLoader(), TestContext.Current.CancellationToken);

        Assert.Contains(permissionId, cached);
    }

    [Fact]
    public async Task Set_ExistingEntry_ReplacesItAndRestartsTheLifetime()
    {
        var clock = new MutableTimeProvider();
        var cache = CreateCache(clock);
        var userId = Guid.NewGuid();
        var replacementPermissionId = Guid.NewGuid();

        await cache.GetOrLoadAsync(userId, Loader(Guid.NewGuid()), TestContext.Current.CancellationToken);
        clock.Advance(_lifetime - TimeSpan.FromMinutes(1));
        cache.Set(userId, cache.CaptureInvalidationStamp(), [replacementPermissionId]);

        // Past the original entry's expiry but inside the replacement's, so a hit proves the
        // lifetime restarted rather than being inherited.
        clock.Advance(TimeSpan.FromMinutes(2));
        var cached = await cache.GetOrLoadAsync(userId, ThrowingLoader(), TestContext.Current.CancellationToken);

        Assert.Equal([replacementPermissionId], cached);
    }

    [Fact]
    public async Task Set_InvalidatedSinceTheStampWasCaptured_IsSkipped()
    {
        // The sign-in path reads grants, then writes them back. A revoke committing in between must
        // win, or the pre-revoke snapshot would restore access for a whole entry lifetime.
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var revokedPermissionId = Guid.NewGuid();
        var reloadedPermissionId = Guid.NewGuid();

        var stamp = cache.CaptureInvalidationStamp();
        cache.Invalidate(userId);
        cache.Set(userId, stamp, [revokedPermissionId]);

        var next = await cache.GetOrLoadAsync(userId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);

        Assert.Equal([reloadedPermissionId], next);
    }

    [Fact]
    public async Task Set_AnotherUserInvalidatedSinceTheStamp_IsSkipped()
    {
        // The stamp is global, so an unrelated eviction also skips the write. That is deliberate:
        // it costs one load and keeps the ordering check to a single comparison. Seeding an entry
        // first pins that a skip *drops* it rather than leaving something even staler to be served.
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var reloadedPermissionId = Guid.NewGuid();

        cache.Set(userId, cache.CaptureInvalidationStamp(), [Guid.NewGuid()]);

        var stamp = cache.CaptureInvalidationStamp();
        cache.Invalidate(Guid.NewGuid());
        cache.Set(userId, stamp, [permissionId]);

        var next = await cache.GetOrLoadAsync(userId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);

        Assert.Equal([reloadedPermissionId], next);
    }

    [Fact]
    public async Task Set_ClearedSinceTheStampWasCaptured_IsSkipped()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var reloadedPermissionId = Guid.NewGuid();

        var stamp = cache.CaptureInvalidationStamp();
        cache.Clear();
        cache.Set(userId, stamp, [Guid.NewGuid()]);

        var next = await cache.GetOrLoadAsync(userId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);

        Assert.Equal([reloadedPermissionId], next);
    }

    [Fact]
    public async Task Set_EmptyPermissionIds_CachesTheEmptySet()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();

        cache.Set(userId, cache.CaptureInvalidationStamp(), []);
        var cached = await cache.GetOrLoadAsync(userId, ThrowingLoader(), TestContext.Current.CancellationToken);

        Assert.Empty(cached);
    }

    [Fact]
    public async Task Invalidate_SingleUser_ForcesAReloadForThatUserOnly()
    {
        var cache = CreateCache();
        var invalidatedUserId = Guid.NewGuid();
        var untouchedUserId = Guid.NewGuid();
        var untouchedPermissionId = Guid.NewGuid();
        var reloadedPermissionId = Guid.NewGuid();

        cache.Set(invalidatedUserId, cache.CaptureInvalidationStamp(), [Guid.NewGuid()]);
        cache.Set(untouchedUserId, cache.CaptureInvalidationStamp(), [untouchedPermissionId]);

        cache.Invalidate(invalidatedUserId);

        var reloaded = await cache.GetOrLoadAsync(
            invalidatedUserId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);
        var untouched = await cache.GetOrLoadAsync(
            untouchedUserId, ThrowingLoader(), TestContext.Current.CancellationToken);

        Assert.Equal([reloadedPermissionId], reloaded);
        Assert.Equal([untouchedPermissionId], untouched);
    }

    [Fact]
    public async Task Invalidate_ManyUsers_ForcesAReloadForEach()
    {
        var cache = CreateCache();
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var reloadedPermissionId = Guid.NewGuid();

        cache.Set(firstUserId, cache.CaptureInvalidationStamp(), [Guid.NewGuid()]);
        cache.Set(secondUserId, cache.CaptureInvalidationStamp(), [Guid.NewGuid()]);

        cache.Invalidate([firstUserId, secondUserId, Guid.NewGuid()]);

        var first = await cache.GetOrLoadAsync(firstUserId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);
        var second = await cache.GetOrLoadAsync(secondUserId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);

        Assert.Equal([reloadedPermissionId], first);
        Assert.Equal([reloadedPermissionId], second);
    }

    [Fact]
    public void Invalidate_UnknownUser_IsANoOp()
    {
        var cache = CreateCache();

        cache.Invalidate(Guid.NewGuid());
        cache.Invalidate([Guid.NewGuid(), Guid.NewGuid()]);
    }

    [Fact]
    public async Task Invalidate_DuringAnInFlightLoad_DoesNotPublishTheStaleResult()
    {
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var stalePermissionId = Guid.NewGuid();
        var freshPermissionId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<IReadOnlyCollection<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loading = cache.GetOrLoadAsync(
            userId,
            async ct =>
            {
                started.TrySetResult();
                return await tcs.Task.WaitAsync(ct);
            },
            TestContext.Current.CancellationToken);

        await started.Task;
        cache.Invalidate(userId);
        tcs.SetResult([stalePermissionId]);
        await loading;

        var next = await cache.GetOrLoadAsync(userId, Loader(freshPermissionId), TestContext.Current.CancellationToken);

        Assert.Equal([freshPermissionId], next);
    }

    [Fact]
    public async Task Clear_DropsEveryEntry()
    {
        var cache = CreateCache();
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var reloadedPermissionId = Guid.NewGuid();

        cache.Set(firstUserId, cache.CaptureInvalidationStamp(), [Guid.NewGuid()]);
        cache.Set(secondUserId, cache.CaptureInvalidationStamp(), [Guid.NewGuid()]);

        cache.Clear();

        var first = await cache.GetOrLoadAsync(firstUserId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);
        var second = await cache.GetOrLoadAsync(secondUserId, Loader(reloadedPermissionId), TestContext.Current.CancellationToken);

        Assert.Equal([reloadedPermissionId], first);
        Assert.Equal([reloadedPermissionId], second);
    }
}
