using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Settings;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Caching;

public class McpClientCacheTests
{
    private static McpClientCache CreateCache(McpClientCacheOptions? options = null)
    {
        // A long sweep interval keeps the background sweep inert during these short tests.
        options ??= new McpClientCacheOptions { SweepInterval = TimeSpan.FromHours(1) };
        return new McpClientCache(NullLogger<McpClientCache>.Instance, Options.Create(options));
    }

    private static McpCacheEntrySource CreateSource(
        IAsyncDisposable client, DateTimeOffset? tokenExpiresOn = null)
    {
        // A fresh list per source lets tests prove co-waiters share one entry by reference.
        return new McpCacheEntrySource
        {
            Client = client,
            Tools = new List<AITool>(),
            TokenExpiresOn = tokenExpiresOn
        };
    }

    /// <summary>
    /// Retries the assertion for up to ~1 second because the cache disposes evicted clients
    /// on a fire-and-forget task.
    /// </summary>
    private static async Task AssertEventuallyAsync(Action assertion)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (attempt < 50)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task GetOrCreateAsync_ConcurrentCallersSameKey_RunSingleFactoryAndShareEntry()
    {
        await using var cache = CreateCache();
        var key = McpCacheKey.ForUser(Guid.NewGuid(), Guid.NewGuid());
        var client = Substitute.For<IAsyncDisposable>();
        var tcs = new TaskCompletionSource<McpCacheEntrySource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        Func<CancellationToken, Task<McpCacheEntrySource>> factory = async ct =>
        {
            Interlocked.Increment(ref invocations);
            return await tcs.Task.WaitAsync(ct);
        };

        var first = cache.GetOrCreateAsync(key, factory, TestContext.Current.CancellationToken);
        var second = cache.GetOrCreateAsync(key, factory, TestContext.Current.CancellationToken);
        tcs.SetResult(CreateSource(client));

        await using var lease1 = await first;
        await using var lease2 = await second;

        Assert.Equal(1, invocations);
        Assert.Same(lease1.Tools, lease2.Tools);
    }

    [Fact]
    public async Task GetOrCreateAsync_DifferentKeys_RunSeparateFactories()
    {
        await using var cache = CreateCache();
        var serverId = Guid.NewGuid();
        var userKey = McpCacheKey.ForUser(serverId, Guid.NewGuid());
        var otherUserKey = McpCacheKey.ForUser(serverId, Guid.NewGuid());
        var sharedKey = McpCacheKey.Shared(Guid.NewGuid());
        var invocations = 0;
        Func<CancellationToken, Task<McpCacheEntrySource>> factory = ct =>
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(CreateSource(Substitute.For<IAsyncDisposable>()));
        };

        await using var lease1 = await cache.GetOrCreateAsync(userKey, factory, TestContext.Current.CancellationToken);
        await using var lease2 = await cache.GetOrCreateAsync(otherUserKey, factory, TestContext.Current.CancellationToken);
        await using var lease3 = await cache.GetOrCreateAsync(sharedKey, factory, TestContext.Current.CancellationToken);

        Assert.Equal(3, invocations);
        Assert.NotSame(lease1.Tools, lease2.Tools);
        Assert.NotSame(lease1.Tools, lease3.Tools);
    }

    [Fact]
    public async Task GetOrCreateAsync_FactoryFails_PropagatesAndNextCallRunsFreshFactory()
    {
        await using var cache = CreateCache();
        var key = McpCacheKey.Shared(Guid.NewGuid());
        var client = Substitute.For<IAsyncDisposable>();
        var invocations = 0;
        Func<CancellationToken, Task<McpCacheEntrySource>> factory = ct =>
        {
            if (Interlocked.Increment(ref invocations) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(CreateSource(client));
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrCreateAsync(key, factory, TestContext.Current.CancellationToken));
        Assert.Equal("boom", exception.Message);

        await using var lease = await cache.GetOrCreateAsync(key, factory, TestContext.Current.CancellationToken);

        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task GetOrCreateAsync_CreatorCancels_CoWaiterRetriesWithOwnFactory()
    {
        await using var cache = CreateCache();
        var key = McpCacheKey.Shared(Guid.NewGuid());
        using var creatorCts = new CancellationTokenSource();
        var never = new TaskCompletionSource<McpCacheEntrySource>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<McpCacheEntrySource>> hangingFactory =
            async ct => await never.Task.WaitAsync(ct);

        // The creator starts the factory under its own token, then cancels while pending.
        var creator = cache.GetOrCreateAsync(key, hangingFactory, creatorCts.Token);

        var coWaiterInvocations = 0;
        var client = Substitute.For<IAsyncDisposable>();
        Func<CancellationToken, Task<McpCacheEntrySource>> coWaiterFactory = ct =>
        {
            Interlocked.Increment(ref coWaiterInvocations);
            return Task.FromResult(CreateSource(client));
        };
        var coWaiter = cache.GetOrCreateAsync(key, coWaiterFactory, TestContext.Current.CancellationToken);

        creatorCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);
        await using var lease = await coWaiter;
        Assert.Equal(1, coWaiterInvocations);
    }

    [Fact]
    public async Task GetOrCreateAsync_CachedEntryInsideMinRemainingLifetime_RecreatesAndDefersOldClientDisposal()
    {
        var options = new McpClientCacheOptions
        {
            TokenExpiryBuffer = TimeSpan.FromMinutes(5),
            MinRemainingLifetimeForStream = TimeSpan.FromMinutes(10),
            MaxEntryLifetime = TimeSpan.FromMinutes(30),
            SweepInterval = TimeSpan.FromHours(1)
        };
        await using var cache = CreateCache(options);
        var key = McpCacheKey.Shared(Guid.NewGuid());
        var oldClient = Substitute.For<IAsyncDisposable>();
        var newClient = Substitute.For<IAsyncDisposable>();
        var invocations = 0;
        Func<CancellationToken, Task<McpCacheEntrySource>> factory = ct =>
        {
            var client = Interlocked.Increment(ref invocations) == 1 ? oldClient : newClient;

            // EffectiveExpiry lands one minute from now — already inside the
            // MinRemainingLifetimeForStream window for any later cache hit.
            return Task.FromResult(CreateSource(
                client, DateTimeOffset.UtcNow + options.TokenExpiryBuffer + TimeSpan.FromMinutes(1)));
        };

        // A freshly created entry is always served, even with a short-lived token.
        var lease1 = await cache.GetOrCreateAsync(key, factory, TestContext.Current.CancellationToken);
        Assert.Equal(1, invocations);

        // The second call sees a cached entry inside the window: it evicts and recreates.
        await using var lease2 = await cache.GetOrCreateAsync(key, factory, TestContext.Current.CancellationToken);
        Assert.Equal(2, invocations);

        _ = oldClient.DidNotReceive().DisposeAsync();

        await lease1.DisposeAsync();

        await AssertEventuallyAsync(() => _ = oldClient.Received(1).DisposeAsync());
        _ = newClient.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task InvalidateServer_MixedEntries_EvictsOnlyThatServerAndDefersLeasedDisposal()
    {
        await using var cache = CreateCache();
        var serverId = Guid.NewGuid();
        var otherServerId = Guid.NewGuid();
        var leasedClient = Substitute.For<IAsyncDisposable>();
        var unleasedClient = Substitute.For<IAsyncDisposable>();
        var otherClient = Substitute.For<IAsyncDisposable>();
        var otherInvocations = 0;
        Func<CancellationToken, Task<McpCacheEntrySource>> otherFactory = ct =>
        {
            Interlocked.Increment(ref otherInvocations);
            return Task.FromResult(CreateSource(otherClient));
        };

        var leased = await cache.GetOrCreateAsync(
            McpCacheKey.ForUser(serverId, Guid.NewGuid()),
            ct => Task.FromResult(CreateSource(leasedClient)),
            TestContext.Current.CancellationToken);
        var unleased = await cache.GetOrCreateAsync(
            McpCacheKey.Shared(serverId),
            ct => Task.FromResult(CreateSource(unleasedClient)),
            TestContext.Current.CancellationToken);
        await unleased.DisposeAsync();
        var other = await cache.GetOrCreateAsync(
            McpCacheKey.Shared(otherServerId), otherFactory, TestContext.Current.CancellationToken);
        await other.DisposeAsync();

        cache.InvalidateServer(serverId);

        await AssertEventuallyAsync(() => _ = unleasedClient.Received(1).DisposeAsync());
        _ = leasedClient.DidNotReceive().DisposeAsync();
        _ = otherClient.DidNotReceive().DisposeAsync();

        // The other server's entry stayed cached: a follow-up hit runs no new factory.
        await using var otherAgain = await cache.GetOrCreateAsync(
            McpCacheKey.Shared(otherServerId), otherFactory, TestContext.Current.CancellationToken);
        Assert.Equal(1, otherInvocations);

        // The leased client is disposed only after its last lease releases.
        await leased.DisposeAsync();
        await AssertEventuallyAsync(() => _ = leasedClient.Received(1).DisposeAsync());
    }

    [Fact]
    public async Task InvalidateServerForUser_OneUsersEntry_LeavesEveryOtherEntryForThatServer()
    {
        await using var cache = CreateCache();
        var serverId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var ownClient = Substitute.For<IAsyncDisposable>();
        var otherUserClient = Substitute.For<IAsyncDisposable>();
        var sharedClient = Substitute.For<IAsyncDisposable>();

        var own = await cache.GetOrCreateAsync(
            McpCacheKey.ForUser(serverId, userId),
            ct => Task.FromResult(CreateSource(ownClient)),
            TestContext.Current.CancellationToken);
        await own.DisposeAsync();
        var otherUser = await cache.GetOrCreateAsync(
            McpCacheKey.ForUser(serverId, Guid.NewGuid()),
            ct => Task.FromResult(CreateSource(otherUserClient)),
            TestContext.Current.CancellationToken);
        await otherUser.DisposeAsync();
        var shared = await cache.GetOrCreateAsync(
            McpCacheKey.Shared(serverId),
            ct => Task.FromResult(CreateSource(sharedClient)),
            TestContext.Current.CancellationToken);
        await shared.DisposeAsync();

        cache.InvalidateServerForUser(serverId, userId);

        await AssertEventuallyAsync(() => _ = ownClient.Received(1).DisposeAsync());
        _ = otherUserClient.DidNotReceive().DisposeAsync();
        _ = sharedClient.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task InvalidateServerForUser_NoSuchEntry_DoesNothing()
    {
        await using var cache = CreateCache();

        var exception = Record.Exception(() => cache.InvalidateServerForUser(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LeaseDisposeAsync_CalledTwice_ReleasesSingleLeaseAndClientDisposedOnce()
    {
        await using var cache = CreateCache();
        var key = McpCacheKey.Shared(Guid.NewGuid());
        var client = Substitute.For<IAsyncDisposable>();
        var lease = await cache.GetOrCreateAsync(
            key, ct => Task.FromResult(CreateSource(client)), TestContext.Current.CancellationToken);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        _ = client.DidNotReceive().DisposeAsync();

        // A double release would leave the lease count negative and the eviction would never
        // dispose the client; a single release lets the eviction dispose it exactly once.
        cache.InvalidateServer(key.McpServerId);

        await AssertEventuallyAsync(() => _ = client.Received(1).DisposeAsync());
    }

    [Fact]
    public async Task DisposeAsync_CompletedUnleasedEntryCached_DisposesClient()
    {
        var cache = CreateCache();
        var client = Substitute.For<IAsyncDisposable>();
        var lease = await cache.GetOrCreateAsync(
            McpCacheKey.Shared(Guid.NewGuid()),
            ct => Task.FromResult(CreateSource(client)),
            TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        await cache.DisposeAsync();

        await AssertEventuallyAsync(() => _ = client.Received(1).DisposeAsync());
    }
}
