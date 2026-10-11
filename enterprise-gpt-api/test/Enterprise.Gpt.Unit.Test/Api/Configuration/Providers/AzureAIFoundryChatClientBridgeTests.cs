using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Configuration.Providers;

/// <summary>
/// Pins the request the OpenAI Chat Completions bridge actually puts on the wire for the Azure AI
/// Foundry provider.
/// </summary>
/// <remarks>
/// <c>ConversationService</c> sets <c>ChatOptions.Reasoning</c> from the catalog flag for every provider,
/// so without <see cref="AzureAIFoundryDefaults"/> clearing it this deployment would reject every turn.
/// </remarks>
public sealed class AzureAIFoundryChatClientBridgeTests
{
    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses.Length > 0 ? responses : [TextAnswer()]);

        public List<string> CapturedBodies { get; } = [];

        public string? CapturedBody => CapturedBodies.Count > 0 ? CapturedBodies[0] : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }

    private static string TextAnswer() => """
        {
          "id": "chatcmpl_stub",
          "object": "chat.completion",
          "created": 1,
          "model": "stub",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "ok" },
              "finish_reason": "stop"
            }
          ],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;

    private static string ToolCall() => """
        {
          "id": "chatcmpl_tool",
          "object": "chat.completion",
          "created": 1,
          "model": "stub",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": null,
                "tool_calls": [
                  {
                    "id": "call_1",
                    "type": "function",
                    "function": { "name": "get_stub", "arguments": "{}" }
                  }
                ]
              },
              "finish_reason": "tool_calls"
            }
          ],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;

    private static AzureAIFoundryOptions Settings() => new()
    {
        Enabled = true,
        Url = "https://test.services.ai.azure.com/",
        ApiKey = "test-key",
        DefaultModel = "provider-default"
    };

    private static IChatClient CreateClient(AzureAIFoundryOptions settings, HttpClient httpClient) =>
        new OpenAIClient(
                new ApiKeyCredential(settings.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = settings.V1Endpoint,
                    Transport = new HttpClientPipelineTransport(httpClient)
                })
            .GetChatClient(settings.DefaultModel)
            .AsIChatClient();

    private static async Task<JsonElement> SendAsync(ChatOptions options)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var settings = Settings();

        var chatClient = CreateClient(settings, httpClient);

        AzureAIFoundryDefaults.Create()(options);

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")],
            options,
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.CapturedBody);

        return JsonDocument.Parse(handler.CapturedBody).RootElement.Clone();
    }

    private static ChatOptions CatalogOptions(bool isReasoningEnabled)
    {
        var options = new ChatOptions
        {
            ModelId = "model-from-catalog",
            ConversationId = "6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatRequestProperties.IsReasoningEnabled] = isReasoningEnabled
            }
        };

        // Mirrors ConversationService for a reasoning-flagged row: the callback has to survive a caller
        // that already asked for reasoning.
        if (isReasoningEnabled)
        {
            options.Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.High,
                Output = ReasoningOutput.Full
            };
        }

        return options;
    }

    [Fact]
    public async Task AsIChatClient_ForAReasoningFlaggedModel_SendsNoReasoningEffort()
    {
        var root = await SendAsync(CatalogOptions(isReasoningEnabled: true));

        Assert.Equal("model-from-catalog", root.GetProperty("model").GetString());

        // Absent, not switched off: a deployment without reasoning rejects the field outright.
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));

        // The catalog gate is ours. The adapter forwards only some AdditionalProperties, but that is
        // package behaviour a bump could change.
        Assert.DoesNotContain(ChatRequestProperties.IsReasoningEnabled, root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsIChatClient_Always_LeavesTheConversationIdOffTheWire()
    {
        // Unlike the Responses provider, ConversationId is not cleared: Chat Completions has nowhere to
        // put it, and this verifies that rather than assuming it.
        var root = await SendAsync(CatalogOptions(isReasoningEnabled: false));

        Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.False(root.TryGetProperty("conversation", out _));
        Assert.DoesNotContain("6f9d1c1e", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsIChatClient_AcrossAToolCall_CompletesTheExchange()
    {
        var handler = new RecordingHandler(ToolCall(), TextAnswer());
        using var httpClient = new HttpClient(handler);
        var settings = Settings();

        var chatClient = CreateClient(settings, httpClient)
            .AsBuilder()
            .UseFunctionInvocation()
            .Use((inner, _) => new ConfigureOptionsChatClient(inner, AzureAIFoundryDefaults.Create()))
            .Build();

        using (chatClient)
        {
            var options = CatalogOptions(isReasoningEnabled: true);
            options.Tools = [AIFunctionFactory.Create(() => "sunny", "get_stub", "A stub tool.")];

            var response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Hello")],
                options,
                TestContext.Current.CancellationToken);

            Assert.Equal("ok", response.Text);
        }

        Assert.Equal(2, handler.CapturedBodies.Count);

        // The second turn is the one that would carry a smuggled reasoning_effort, since the defaults
        // callback runs again on the function-invoking client's follow-up request.
        foreach (var body in handler.CapturedBodies)
        {
            var root = JsonDocument.Parse(body).RootElement;
            Assert.False(root.TryGetProperty("reasoning_effort", out _));
        }
    }
}
