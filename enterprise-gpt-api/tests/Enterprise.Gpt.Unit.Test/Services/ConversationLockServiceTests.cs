using Enterprise.Gpt.Service;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

/// <summary>
/// Covers <see cref="ConversationLockService"/>: that it grants a conversation to exactly one
/// caller at a time, that the registry drains as holders release, and that it survives the
/// concurrent acquire/release churn a chat workload produces.
/// </summary>
public sealed class ConversationLockServiceTests
{
    [Fact]
    public async Task TryAcquireLockAsync_WhenNotHeld_ReturnsReleaser()
    {
        using var service = new ConversationLockService();

        using var releaser = await service.TryAcquireLockAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.NotNull(releaser);
    }

    [Fact]
    public async Task TryAcquireLockAsync_WhenAlreadyHeld_ReturnsNull()
    {
        using var service = new ConversationLockService();
        var conversationId = Guid.NewGuid();

        using var first = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);
        var second = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task TryAcquireLockAsync_AfterRelease_AcquiresAgain()
    {
        using var service = new ConversationLockService();
        var conversationId = Guid.NewGuid();

        var first = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);
        first!.Dispose();
        using var second = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task TryAcquireLockAsync_ForDifferentConversations_BothAcquire()
    {
        using var service = new ConversationLockService();

        using var first = await service.TryAcquireLockAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        using var second = await service.TryAcquireLockAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    /// <summary>
    /// Disposal must be idempotent: an unguarded second release either faults on the semaphore
    /// the first release already reclaimed, or — if another caller re-took the entry in between —
    /// raises its count to two and admits a second holder alongside the current one.
    /// </summary>
    [Fact]
    public async Task Dispose_CalledTwice_ReleasesSemaphoreOnce()
    {
        using var service = new ConversationLockService();
        var conversationId = Guid.NewGuid();

        var releaser = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);
        releaser!.Dispose();
        releaser.Dispose();

        using var next = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);
        var alongside = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);

        Assert.NotNull(next);
        Assert.Null(alongside);
    }

    [Fact]
    public async Task TryAcquireLockAsync_AfterAllHoldersRelease_RemovesEntry()
    {
        using var service = new ConversationLockService();
        var conversationId = Guid.NewGuid();

        var releaser = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);
        Assert.Equal(1, service.ActiveLockCount);

        releaser!.Dispose();

        Assert.Equal(0, service.ActiveLockCount);
    }

    /// <summary>
    /// A failed acquisition still takes a reference to the registry entry while it probes the
    /// semaphore; that reference has to be given back or the entry outlives its holder.
    /// </summary>
    [Fact]
    public async Task TryAcquireLockAsync_WhenContended_DoesNotLeakEntryForTheLoser()
    {
        using var service = new ConversationLockService();
        var conversationId = Guid.NewGuid();

        var holder = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);
        var loser = await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken);
        Assert.Null(loser);

        holder!.Dispose();

        Assert.Equal(0, service.ActiveLockCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(64)]
    public async Task TryAcquireLockAsync_UnderConcurrentContention_OnlyOneSucceeds(int contenders)
    {
        using var service = new ConversationLockService();
        var conversationId = Guid.NewGuid();

        var attempts = await Task.WhenAll(Enumerable.Range(0, contenders).Select(_ =>
            Task.Run(async () => await service.TryAcquireLockAsync(conversationId, TestContext.Current.CancellationToken))));

        var winners = attempts.Where(releaser => releaser is not null).ToList();
        Assert.Single(winners);

        foreach (var winner in winners)
        {
            winner!.Dispose();
        }

        Assert.Equal(0, service.ActiveLockCount);
    }

    /// <summary>
    /// Regression for the eviction race the reference-counted registry replaced: a sweep that
    /// removed and disposed entries on a timer could dispose a semaphore a caller had just been
    /// handed, faulting that caller and letting the next one acquire a fresh entry for a
    /// conversation that was already in use.
    /// </summary>
    [Fact]
    public async Task TryAcquireLockAsync_UnderRepeatedChurn_DoesNotFaultAndDrains()
    {
        using var service = new ConversationLockService();
        var conversationIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        // Counted per conversation, not in aggregate: distinct conversations are expected to be
        // held simultaneously, and only same-conversation overlap is a violation.
        var concurrentHolders = new int[conversationIds.Length];

        await Task.WhenAll(Enumerable.Range(0, 32).Select(worker => Task.Run(async () =>
        {
            var index = worker % conversationIds.Length;

            for (var i = 0; i < 200; i++)
            {
                var releaser = await service.TryAcquireLockAsync(conversationIds[index], TestContext.Current.CancellationToken);
                if (releaser is null)
                {
                    continue;
                }

                Assert.Equal(1, Interlocked.Increment(ref concurrentHolders[index]));
                // Yield while holding, so the window in which an overlap could be observed is
                // wide enough to actually catch one rather than closing in a few nanoseconds.
                await Task.Yield();
                Assert.Equal(0, Interlocked.Decrement(ref concurrentHolders[index]));
                releaser.Dispose();
            }
        })));

        Assert.Equal(0, service.ActiveLockCount);
    }

    /// <summary>
    /// A cancelled acquisition never reaches the releaser, so the reference it took while probing
    /// the semaphore has to be handed back on the exception path too.
    /// </summary>
    [Fact]
    public async Task TryAcquireLockAsync_WhenCancelled_ThrowsAndDoesNotLeakEntry()
    {
        using var service = new ConversationLockService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.TryAcquireLockAsync(Guid.NewGuid(), cts.Token));

        Assert.Equal(0, service.ActiveLockCount);
    }

    [Fact]
    public async Task TryAcquireLockAsync_AfterDispose_Throws()
    {
        var service = new ConversationLockService();
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await service.TryAcquireLockAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var service = new ConversationLockService();

        service.Dispose();
        service.Dispose();
    }

    /// <summary>
    /// Host shutdown must not fault a turn that is still unwinding, so an entry held by a live
    /// releaser is left for that releaser to dispose rather than torn down underneath it.
    /// </summary>
    [Fact]
    public async Task Dispose_WithHeldLock_LeavesHolderAbleToRelease()
    {
        var service = new ConversationLockService();
        var releaser = await service.TryAcquireLockAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        service.Dispose();

        releaser!.Dispose();
    }
}
