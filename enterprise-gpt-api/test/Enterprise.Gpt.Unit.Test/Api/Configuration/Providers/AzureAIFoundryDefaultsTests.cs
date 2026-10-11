using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Common.Constants;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Configuration.Providers;

/// <summary>
/// The callback's decisions in isolation; <see cref="AzureAIFoundryChatClientBridgeTests"/> covers the wire.
/// </summary>
public sealed class AzureAIFoundryDefaultsTests
{
    private static ChatOptions Apply(ChatOptions options)
    {
        AzureAIFoundryDefaults.Create()(options);

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
    public void Create_Always_ClearsTheReasoningRequest(bool isReasoningEnabled)
    {
        // ConversationService sets Reasoning for a reasoning-flagged row on any provider; this is the
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
        // Deliberately unlike AzureOpenAIDefaults: the Responses adapter reads ConversationId as
        // previous_response_id, but Chat Completions has nowhere to put it.
        var options = CatalogOptions(isReasoningEnabled: false);
        options.ConversationId = "6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b";

        Assert.Equal("6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b", Apply(options).ConversationId);
    }

    [Fact]
    public void Create_Always_LeavesTheModelIdAlone()
    {
        // Every turn names its deployment from the catalog, never the provider default.
        Assert.Equal("model-from-catalog", Apply(CatalogOptions(isReasoningEnabled: false)).ModelId);
    }

    [Fact]
    public void Create_Always_LeavesTheRawRepresentationFactoryAlone()
    {
        // This provider has nothing to stamp onto the SDK request type, so it must not install a
        // factory — doing so would silently displace one a caller supplied for a single turn.
        Assert.Null(Apply(CatalogOptions(isReasoningEnabled: true)).RawRepresentationFactory);
    }
}
