using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Enterprise.Gpt.Api.Chat;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Chat;

/// <summary>
/// Pins the request the OpenAI Chat Completions bridge actually puts on the wire for the Azure AI
/// Foundry provider.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing assertion is the absence of <c>reasoning_effort</c> even when the catalog row
/// asks for reasoning. <c>ConversationService</c> populates <c>ChatOptions.Reasoning</c> from that
/// flag for every provider, and this adapter translates it — so without
/// <see cref="AzureAIFoundryChatDefaults"/> clearing it, a reasoning-flagged model pointed at this
/// provider would send an option the deployment rejects, on every turn.
/// </para>
/// <para>
/// Note the contrast with <see cref="AzureOpenAIChatClientBridgeTests"/>: nothing here needs an
/// <c>OPENAI001</c> or <c>MEAI001</c> suppression, because <c>GetChatClient</c> and its
/// <c>AsIChatClient</c> overload are both stable. That is the split working as intended.
/// </para>
/// <para>
/// No network is involved: the request is intercepted by a stub transport that records the body and
/// answers with a minimal completed chat completion.
/// </para>
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

        AzureAIFoundryChatDefaults.Create()(options);

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

        // Mirrors what ConversationService does with a reasoning-flagged catalog row. Setting it here
        // rather than asserting against a bare ChatOptions is the whole point: the defaults callback
        // has to survive a caller that already asked for reasoning.
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

        // Not "sends it switched off": the field is absent. A Chat Completions deployment that does
        // not support reasoning rejects the request rather than ignoring the field.
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));

        // The catalog gate is ours, not the API's. The adapter forwards only a known subset of
        // AdditionalProperties today, but that is package behaviour rather than a guarantee, and a
        // bump that started forwarding the rest would put this key on every request.
        Assert.DoesNotContain(ChatRequestProperties.IsReasoningEnabled, root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsIChatClient_ForANonReasoningModel_SendsNoReasoningEffort()
    {
        var root = await SendAsync(CatalogOptions(isReasoningEnabled: false));

        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task AsIChatClient_Always_TakesTheModelFromTheCatalogRatherThanTheClientDefault()
    {
        // The client is constructed with DefaultModel purely because the SDK requires one; every turn
        // overrides it from the catalog, and a regression here would silently serve one deployment's
        // traffic from another.
        var root = await SendAsync(CatalogOptions(isReasoningEnabled: false));

        Assert.Equal("model-from-catalog", root.GetProperty("model").GetString());
        Assert.NotEqual("provider-default", root.GetProperty("model").GetString());
    }

    [Fact]
    public async Task AsIChatClient_Always_LeavesTheConversationIdOffTheWire()
    {
        // Unlike the Responses provider, this one does not clear ChatOptions.ConversationId — it does
        // not have to, because Chat Completions is stateless and the adapter has nowhere to put it.
        // Asserting the id is genuinely absent from the body is what makes "does not have to" a
        // verified claim rather than an assumption about adapter internals.
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
            .Use((inner, _) => new ConfigureOptionsChatClient(inner, AzureAIFoundryChatDefaults.Create()))
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
