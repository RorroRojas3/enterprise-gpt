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
/// Pins the request the OpenAI Responses bridge actually puts on the wire for the Azure AI Foundry
/// provider.
/// </summary>
/// <remarks>
/// <para>
/// Two of the things this asserts are invisible anywhere else. The first is that
/// <c>previous_response_id</c> is absent: <c>ChatOptions.ConversationId</c> was inert under Chat
/// Completions, but the Responses adapter reads any id that does not begin with <c>conv_</c> as a
/// response id and sends it as that — so the conversation GUID this application sets would ask the
/// service to continue a response it never issued, on every turn. The second is that the reasoning
/// options appear only for a model whose catalog row asks for them, because a deployment that does
/// not support them rejects the whole request.
/// </para>
/// <para>
/// No network is involved: the request is intercepted by a stub transport that records the body and
/// answers with a minimal completed response.
/// </para>
/// </remarks>
public sealed class AzureFoundryChatClientBridgeTests
{
    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses.Length > 0 ? responses : [TextAnswer("resp_1")]);

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

    private static string TextAnswer(string id) => $$"""
        {
          "id": "{{id}}",
          "object": "response",
          "created_at": 1,
          "status": "completed",
          "model": "stub",
          "output": [
            {
              "type": "message",
              "id": "msg_stub",
              "status": "completed",
              "role": "assistant",
              "content": [{ "type": "output_text", "text": "ok", "annotations": [] }]
            }
          ],
          "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
        }
        """;

    private static string ToolCall(string id) => $$"""
        {
          "id": "{{id}}",
          "object": "response",
          "created_at": 1,
          "status": "completed",
          "model": "stub",
          "output": [
            {
              "type": "function_call",
              "id": "fc_1",
              "call_id": "call_1",
              "name": "get_stub",
              "arguments": "{}",
              "status": "completed"
            }
          ],
          "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
        }
        """;

    private static AzureAIFoundryOptions Settings() => new()
    {
        Url = "https://test.services.ai.azure.com/",
        ApiKey = "test-key",
        DefaultModel = "provider-default",
        EmbeddingModel = "test-embedding",
        ReasoningSummary = "auto",
        ReasoningEffort = "high"
    };

    private static async Task<JsonElement> SendAsync(AzureAIFoundryOptions settings, ChatOptions options)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);

#pragma warning disable OPENAI001, MEAI001
        var chatClient = new OpenAIClient(
                new ApiKeyCredential(settings.ApiKey),
                new OpenAIClientOptions { Endpoint = settings.V1Endpoint, Transport = new HttpClientPipelineTransport(httpClient) })
            .GetResponsesClient()
            .AsIChatClient(settings.DefaultModel);
#pragma warning restore OPENAI001, MEAI001

        AzureFoundryChatDefaults.Create(settings)(options);

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")],
            options,
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.CapturedBody);

        return JsonDocument.Parse(handler.CapturedBody).RootElement.Clone();
    }

    private static ChatOptions CatalogOptions(bool isReasoningEnabled) => new()
    {
        ModelId = "model-from-catalog",
        ConversationId = "6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b",
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatRequestProperties.IsReasoningEnabled] = isReasoningEnabled
        }
    };

    [Fact]
    public async Task AsIChatClient_ForAReasoningModel_AsksForASummaryAndSendsNoPreviousResponseId()
    {
        var root = await SendAsync(Settings(), CatalogOptions(isReasoningEnabled: true));

        Assert.Equal("model-from-catalog", root.GetProperty("model").GetString());

        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("auto", reasoning.GetProperty("summary").GetString());
        Assert.Equal("high", reasoning.GetProperty("effort").GetString());

        // The conversation GUID must not have reached the wire as a response id.
        Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.False(root.TryGetProperty("conversation", out _));

        // The gate is ours, not the API's. The adapter reads only a "strict" key off
        // AdditionalProperties today, but that is package behaviour rather than a guarantee, and a
        // bump that started forwarding the rest would put this on every request.
        Assert.DoesNotContain(ChatRequestProperties.IsReasoningEnabled, root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsIChatClient_ForANonReasoningModel_SendsNoReasoningOptionsAtAll()
    {
        // Not "sends them switched off": the option is absent, because a deployment that does not
        // support reasoning rejects the request rather than ignoring the field.
        var root = await SendAsync(Settings(), CatalogOptions(isReasoningEnabled: false));

        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("previous_response_id", out _));
    }

    [Fact]
    public async Task AsIChatClient_WhenSummariesAreTurnedOff_StillReasonsButAsksForNoSummary()
    {
        var settings = Settings();
        settings.ReasoningSummary = "none";

        var root = await SendAsync(settings, CatalogOptions(isReasoningEnabled: true));

        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("high", reasoning.GetProperty("effort").GetString());
        Assert.False(reasoning.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task AsIChatClient_ForANonReasoningModel_IgnoresAProviderAgnosticReasoningRequest()
    {
        // ChatOptions.Reasoning is MEAI's own provider-agnostic reasoning request, and the adapter
        // applies it with `??=` — i.e. only when the raw options carry none, which is exactly the
        // non-reasoning path. Anything that sets it for every turn therefore smuggles reasoning
        // past the catalog gate and onto a deployment that rejects the whole request for it.
        var options = CatalogOptions(isReasoningEnabled: false);
        options.Reasoning = new ReasoningOptions
        {
            Effort = ReasoningEffort.High,
            Output = ReasoningOutput.Full
        };

        var root = await SendAsync(Settings(), options);

        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task AsIChatClient_Always_KeepsTheTurnStateless()
    {
        // The Responses API stores prompts and answers for 30 days unless told otherwise, where
        // Chat Completions stored nothing. Turning it off is what keeps that true after the swap —
        // and it is also what makes the blanket ConversationId clear safe (see the tool-call test).
        var root = await SendAsync(Settings(), CatalogOptions(isReasoningEnabled: false));

        Assert.False(root.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task AsIChatClient_AcrossAToolCall_ResendsTheWholeExchangeRatherThanAnOrphanResult()
    {
        // The regression this exists for: FunctionInvokingChatClient sits *above* the callback, and
        // when the service stores a response it hands back its own id as the ConversationId, drops
        // the local history, and expects the next request to continue from it. Clearing that id
        // without also disabling storage sends a bare function_call_output with nothing to attach
        // it to, which the service rejects — on every tool-calling turn, from the second iteration.
        var handler = new RecordingHandler(ToolCall("resp_1"), TextAnswer("resp_2"));
        using var httpClient = new HttpClient(handler);
        var settings = Settings();

#pragma warning disable OPENAI001, MEAI001
        var chatClient = new OpenAIClient(
                new ApiKeyCredential(settings.ApiKey),
                new OpenAIClientOptions { Endpoint = settings.V1Endpoint, Transport = new HttpClientPipelineTransport(httpClient) })
            .GetResponsesClient()
            .AsIChatClient(settings.DefaultModel)
            .AsBuilder()
            .UseFunctionInvocation()
            .Use((inner, _) => new ConfigureOptionsChatClient(inner, AzureFoundryChatDefaults.Create(settings)))
            .Build();
#pragma warning restore OPENAI001, MEAI001

        using (chatClient)
        {
            var options = CatalogOptions(isReasoningEnabled: false);
            options.Tools = [AIFunctionFactory.Create(() => "sunny", "get_stub", "A stub tool.")];

            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Hello")],
                options,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, handler.CapturedBodies.Count);
        var second = JsonDocument.Parse(handler.CapturedBodies[1]).RootElement;

        Assert.False(second.TryGetProperty("previous_response_id", out _));

        // The second request must carry the exchange that produced the result: the original prompt
        // and the call, not just its output.
        var types = second.GetProperty("input").EnumerateArray()
            .Select(item => item.GetProperty("type").GetString())
            .ToArray();
        Assert.Contains("function_call", types);
        Assert.Contains("function_call_output", types);
        Assert.True(types.Length > 2, $"Expected the prompt to be resent; got [{string.Join(", ", types)}].");
    }
}
