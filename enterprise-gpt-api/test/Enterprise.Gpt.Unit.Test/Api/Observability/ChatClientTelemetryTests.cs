using Enterprise.Gpt.Api.Observability;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Observability;

/// <summary>
/// Pins that the shared pipeline step emits under the source the exporter subscribes to.
/// </summary>
/// <remarks>
/// It cannot stop a sixth provider from calling <c>UseOpenTelemetry()</c> directly — only the extension
/// existing makes that unlikely. What it catches is the extension itself losing the source name, which
/// drops every LLM span and token metric with no build error and no runtime failure.
/// </remarks>
public sealed class ChatClientTelemetryTests : IDisposable
{
    private readonly List<Activity> _activities = [];

    private readonly ActivityListener _listener;

    public ChatClientTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryRegistration.ChatTelemetrySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            // Both samplers: an ambient parent decides which one the runtime consults.
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _activities.Add
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task UseEnterpriseTelemetry_ACompletedTurn_EmitsASpanOnTheExportedSource()
    {
        using var client = new ChatClientBuilder(new StubChatClient())
            .UseEnterpriseTelemetry()
            .Build();

        await client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(_activities);
    }

    private sealed class StubChatClient : IChatClient
    {
        private static readonly ChatClientMetadata ClientMetadata =
            new("stub", new Uri("https://example.invalid"), "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This guard only exercises the non-streaming path.");

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ChatClientMetadata) ? ClientMetadata : null;

        public void Dispose()
        {
        }
    }
}
