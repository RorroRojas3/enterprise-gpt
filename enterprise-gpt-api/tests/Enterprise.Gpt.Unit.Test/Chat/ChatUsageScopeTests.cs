using Andes.Extensions.AI;
using Enterprise.Gpt.Service.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Chat;

/// <summary>
/// Covers <see cref="ChatUsageScope"/> against the real tool-tracking middleware: that a completed
/// request's report reaches the flow that opened the scope, that an abandoned one still does, and
/// that scopes nest without one request's report landing in another's.
/// </summary>
public sealed class ChatUsageScopeTests
{
    private static IChatClient BuildTrackedClient(IChatClient inner)
    {
        return inner
            .AsBuilder()
            .UseToolTracking(options => options.Observers.Add(new ChatUsageObserver()))
            .UseFunctionInvocation()
            .Build();
    }

    /// <summary>
    /// Drains a streamed request the way <c>ConversationService</c> does — by hand, re-entering the
    /// scope around every move and around the disposal — from inside an async iterator, which is
    /// the execution-context shape that makes the re-entry necessary.
    /// </summary>
    private static async IAsyncEnumerable<string> DrainAsync(IChatClient client, ChatUsageScope scope)
    {
        var updates = client.GetStreamingResponseAsync("Hello").GetAsyncEnumerator();

        try
        {
            while (true)
            {
                bool moved;
                using (scope.Enter())
                {
                    moved = await updates.MoveNextAsync();
                }

                if (!moved)
                {
                    break;
                }

                yield return updates.Current.Text;
            }
        }
        finally
        {
            using (scope.Enter())
            {
                await updates.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Enter_StreamedRequestCompletes_CapturesTheReport()
    {
        using var client = BuildTrackedClient(new ScriptedChatClient
        {
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 }
        });

        var scope = ChatUsageScope.Create();
        await foreach (var _ in DrainAsync(client, scope))
        {
        }

        Assert.NotNull(scope.Report);
        Assert.Equal(11, scope.Report.AssistantUsage.InputTokenCount);
        Assert.Equal(7, scope.Report.AssistantUsage.OutputTokenCount);
    }

    // The reason the report is read from the observer rather than off the stream: the in-band
    // report is the stream's last update, and an abandoned turn never reaches it.
    [Fact]
    public async Task Enter_StreamedRequestFaultsAfterUsage_StillCapturesAPartialReport()
    {
        using var client = BuildTrackedClient(new ScriptedChatClient
        {
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 },
            Failure = new HttpRequestException("The provider dropped the connection.")
        });

        var scope = ChatUsageScope.Create();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in DrainAsync(client, scope))
            {
            }
        });

        Assert.NotNull(scope.Report);
        Assert.Equal(11, scope.Report.AssistantUsage.InputTokenCount);
        Assert.Equal(7, scope.Report.AssistantUsage.OutputTokenCount);
    }

    // The consumer walks away part-way through, which is what a client disconnect looks like from
    // here: the account still has to land, and it lands on the enumerator's disposal.
    [Fact]
    public async Task Enter_StreamedRequestAbandonedPartWayThrough_StillCapturesAReport()
    {
        using var client = BuildTrackedClient(new ScriptedChatClient
        {
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 }
        });

        var scope = ChatUsageScope.Create();
        await foreach (var _ in DrainAsync(client, scope))
        {
            break;
        }

        Assert.NotNull(scope.Report);
    }

    [Fact]
    public async Task Enter_NonStreamedRequestCompletes_CapturesTheReport()
    {
        using var client = BuildTrackedClient(new ScriptedChatClient
        {
            Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2 }
        });

        var scope = ChatUsageScope.Create();
        using (scope.Enter())
        {
            await client.GetResponseAsync("Hello", cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.NotNull(scope.Report);
        Assert.Equal(4, scope.Report.AssistantUsage.InputTokenCount);
    }

    [Fact]
    public async Task Enter_NestedActivation_KeepsTheInnerRequestOutOfTheOuterScope()
    {
        using var client = BuildTrackedClient(new ScriptedChatClient
        {
            Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2 }
        });

        var outer = ChatUsageScope.Create();
        var inner = ChatUsageScope.Create();

        using (outer.Enter())
        using (inner.Enter())
        {
            await client.GetResponseAsync("Inner", cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.NotNull(inner.Report);
        Assert.Null(outer.Report);
    }

    [Fact]
    public async Task Enter_NestedActivationDisposed_RestoresTheOuterScopeAsTheTarget()
    {
        using var client = BuildTrackedClient(new ScriptedChatClient
        {
            Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2 }
        });

        var outer = ChatUsageScope.Create();

        using (outer.Enter())
        {
            using (ChatUsageScope.Create().Enter())
            {
            }

            await client.GetResponseAsync("Outer", cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.NotNull(outer.Report);
    }

    // A request issued outside any activation — the document pipeline, or a plain unit test — has
    // nowhere to report to, and that is not an error.
    [Fact]
    public async Task OnRequestCompleted_NoActiveScope_IsASafeNoOp()
    {
        using var client = BuildTrackedClient(new ScriptedChatClient
        {
            Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2 }
        });

        var response = await client.GetResponseAsync("Hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
    }

    [Fact]
    public void Report_NoRequestRan_IsNull()
    {
        Assert.Null(ChatUsageScope.Create().Report);
    }

    /// <summary>
    /// Program.cs registers the observer in the container and never hands it to
    /// <c>UseToolTracking</c> explicitly, relying on the middleware to collect every registered
    /// <see cref="IChatProgressObserver"/> when the client is built. That is the single point of
    /// failure for the whole feature — miss it and every turn silently records zero tokens — so the
    /// contract is pinned here rather than assumed.
    /// </summary>
    [Fact]
    public async Task UseToolTracking_ObserverRegisteredInTheContainer_IsPickedUpWithoutBeingPassedIn()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatProgressObserver, ChatUsageObserver>();
        using var provider = services.BuildServiceProvider();

        using var client = new ScriptedChatClient { Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2 } }
            .AsBuilder()
            .UseToolTracking()
            .UseFunctionInvocation()
            .Build(provider);

        var scope = ChatUsageScope.Create();
        using (scope.Enter())
        {
            await client.GetResponseAsync("Hello", cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.NotNull(scope.Report);
        Assert.Equal(4, scope.Report.AssistantUsage.InputTokenCount);
    }

    /// <summary>
    /// A chat client that answers with one text update and, unless <see cref="Usage"/> is left
    /// unset, a usage update after it.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public UsageDetails? Usage { get; init; }

        /// <summary>
        /// Gets an exception thrown after the usage update, standing in for a provider that drops
        /// the connection having already reported what the turn consumed.
        /// </summary>
        public Exception? Failure { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello"))
            {
                Usage = Usage
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello");
            await Task.Yield();

            if (Usage is not null)
            {
                yield return new ChatResponseUpdate { Contents = [new UsageContent(Usage)] };
            }

            if (Failure is not null)
            {
                throw Failure;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
