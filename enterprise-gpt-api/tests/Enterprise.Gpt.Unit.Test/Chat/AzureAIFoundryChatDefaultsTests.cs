using Enterprise.Gpt.Api.Chat;
using Enterprise.Gpt.Dto.Enums;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Chat;

/// <summary>
/// Covers the callback that shapes an Azure AI Foundry turn, in isolation from the SDK bridge.
/// <see cref="AzureAIFoundryChatClientBridgeTests"/> covers what actually reaches the wire; this
/// covers the decisions the callback makes before it gets there.
/// </summary>
public sealed class AzureAIFoundryChatDefaultsTests
{
    private static ChatOptions Apply(ChatOptions options)
    {
        AzureAIFoundryChatDefaults.Create()(options);

        return options;
    }

    private static ChatOptions CatalogOptions(bool isReasoningEnabled) => new()
    {
        ModelId = "model-from-catalog",
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ChatRequestProperties.IsReasoningEnabled] = isReasoningEnabled
        }
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_Always_ClearsTheReasoningRequest(bool isReasoningEnabled)
    {
        // Unconditional on purpose, and the `true` case is the one that matters: the catalog can flag
        // a model on this provider as reasoning-enabled, and ConversationService will honour that by
        // populating ChatOptions.Reasoning regardless of which provider serves the turn. This is the
        // only place that combination is discarded.
        var options = CatalogOptions(isReasoningEnabled);
        options.Reasoning = new ReasoningOptions
        {
            Effort = ReasoningEffort.High,
            Output = ReasoningOutput.Full
        };

        Assert.Null(Apply(options).Reasoning);
    }

    [Fact]
    public void Create_Always_LeavesTheConversationIdAlone()
    {
        // The asymmetry with AzureOpenAIChatDefaults is deliberate, not an oversight. The Responses
        // adapter reads ConversationId as previous_response_id and has to have it cleared; Chat
        // Completions is stateless and the adapter has nowhere to put it, so there is nothing to
        // clear. AzureAIFoundryChatClientBridgeTests pins that it stays off the wire regardless.
        var options = CatalogOptions(isReasoningEnabled: false);
        options.ConversationId = "6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b";

        Assert.Equal("6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b", Apply(options).ConversationId);
    }

    [Fact]
    public void Create_Always_LeavesTheModelIdAlone()
    {
        // Every turn names its deployment from the catalog; the callback must not substitute the
        // provider default.
        Assert.Equal("model-from-catalog", Apply(CatalogOptions(isReasoningEnabled: false)).ModelId);
    }

    [Fact]
    public void Create_WithNoReasoningRequested_LeavesTheOptionsUsable()
    {
        var options = Apply(new ChatOptions { ModelId = "model-from-catalog" });

        Assert.Null(options.Reasoning);
        Assert.Equal("model-from-catalog", options.ModelId);
    }

    [Fact]
    public void Create_Always_LeavesTheRawRepresentationFactoryAlone()
    {
        // This provider has nothing to stamp onto the SDK request type, so it must not install a
        // factory — doing so would silently displace one a caller supplied for a single turn.
        Assert.Null(Apply(CatalogOptions(isReasoningEnabled: true)).RawRepresentationFactory);
    }
}
