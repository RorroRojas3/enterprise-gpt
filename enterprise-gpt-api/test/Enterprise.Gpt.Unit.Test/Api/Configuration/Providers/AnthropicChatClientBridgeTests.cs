using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Api.Options;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Configuration.Providers;

/// <summary>
/// Pins the request the Anthropic SDK's Microsoft.Extensions.AI bridge actually puts on the wire.
/// </summary>
/// <remarks>
/// The bridge leaves <c>Model</c> and <c>MaxTokens</c> un-overlaid, contrary to its documentation, so a
/// package bump could change that while every isolated <see cref="AnthropicDefaultsTests"/> test stays green.
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

        // No retries, so an unexpected failure surfaces immediately rather than behind the assertion.
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
        AnthropicDefaults.Create(settings)(options);

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
