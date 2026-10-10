using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Enterprise.Gpt.Api.Chat;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Chat;

/// <summary>
/// Pins the request the Anthropic SDK's Microsoft.Extensions.AI bridge actually puts on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AnthropicChatDefaultsTests"/> exercises the callback in isolation, which cannot see the
/// behaviour the callback is written around: the bridge treats the raw parameters as an overlay base
/// and merges messages, tools, and system content onto them, but leaves <c>Model</c> and
/// <c>MaxTokens</c> untouched. That behaviour is the opposite of what the SDK's own documentation for
/// <c>AsIChatClient</c> describes, so it is undocumented and could change in a package bump — and
/// every isolated test would stay green while every conversation silently ran on the wrong model.
/// </para>
/// <para>
/// No network is involved: the request is intercepted by a stub handler that records the body and
/// answers with a minimal message.
/// </para>
/// </remarks>
public sealed class AnthropicChatClientBridgeTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            const string body = """
                {
                  "id": "msg_stub",
                  "type": "message",
                  "role": "assistant",
                  "model": "claude-stub",
                  "content": [{ "type": "text", "text": "ok" }],
                  "stop_reason": "end_turn",
                  "usage": { "input_tokens": 1, "output_tokens": 1 }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task AsIChatClient_WithTheConfiguredDefaults_SendsTheCatalogModelAndTheRequestsTokenCeiling()
    {
        var handler = new RecordingHandler();
        var settings = new AnthropicOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            DefaultModelId = "claude-provider-default",
            DefaultMaxOutputTokens = 4096,
            Thinking = "adaptive",
            Effort = "xhigh"
        };

        // MaxRetries is zeroed so an unexpected non-success answer surfaces immediately instead of
        // being retried behind the assertion.
        using var httpClient = new HttpClient(handler);
        var anthropicClient = new AnthropicClient
        {
            ApiKey = settings.ApiKey,
            MaxRetries = 0,
            HttpClient = httpClient
        };

        var chatClient = anthropicClient
            .AsIChatClient(settings.DefaultModelId, settings.DefaultMaxOutputTokens);

        var options = new ChatOptions { ModelId = "claude-from-catalog", MaxOutputTokens = 1234 };
        AnthropicChatDefaults.Create(settings)(options);

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")],
            options,
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.CapturedBody);
        using var request = JsonDocument.Parse(handler.CapturedBody);
        var root = request.RootElement;

        // The two fields the bridge does not overlay, and the reason the callback materializes them.
        Assert.Equal("claude-from-catalog", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());

        // The empty base list must not have displaced or duplicated the real conversation.
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());

        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("xhigh", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
    }
}
