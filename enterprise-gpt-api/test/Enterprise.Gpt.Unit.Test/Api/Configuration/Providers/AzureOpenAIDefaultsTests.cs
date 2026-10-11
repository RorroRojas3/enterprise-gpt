using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Xunit;

// OPENAI001: the Responses request types are still experimental as of OpenAI 2.13.0.
#pragma warning disable OPENAI001

namespace Enterprise.Gpt.Unit.Test.Api.Configuration.Providers;

/// <summary>
/// The callback's decisions in isolation; <see cref="AzureOpenAIChatClientBridgeTests"/> covers the wire.
/// </summary>
public sealed class AzureOpenAIDefaultsTests
{
    private static AzureOpenAIOptions Settings() => new()
    {
        Url = "https://test.services.ai.azure.com/",
        ApiKey = "test-key",
        DefaultModel = "provider-default",
        EmbeddingModel = "test-embedding"
    };

    private static ChatOptions Apply(AzureOpenAIOptions settings, ChatOptions options)
    {
        AzureOpenAIDefaults.Create(settings)(options);

        return options;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_Always_ClearsTheConversationId(bool isReasoningEnabled)
    {
        // Whatever else the turn asks for. The Responses adapter would send this GUID as a
        // previous_response_id, and the service has never issued it.
        var options = Apply(Settings(), new ChatOptions
        {
            ConversationId = "6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatRequestProperties.IsReasoningEnabled] = isReasoningEnabled
            }
        });

        Assert.Null(options.ConversationId);
    }

    private static CreateResponseOptions RawOptions(ChatOptions options)
    {
        Assert.NotNull(options.RawRepresentationFactory);

        return Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory(null!));
    }

    [Fact]
    public void Create_ForAReasoningModel_AsksForReasoningAndStillDisablesStorage()
    {
        var raw = RawOptions(Apply(Settings(), new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatRequestProperties.IsReasoningEnabled] = true
            }
        }));

        Assert.NotNull(raw.ReasoningOptions);
        Assert.False(raw.StoredOutputEnabled);
        // Stateless mode cannot look a reasoning item up again, so it has to travel with the
        // request or the model rejects its own replayed item on the next tool iteration.
        Assert.NotEmpty(raw.IncludedProperties);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Create_WithoutTheCatalogFlag_AsksForNoReasoningButStillDisablesStorage(bool? isReasoningEnabled)
    {
        // Null is a request that never set the property. Absent means no, since a deployment without
        // reasoning rejects the whole request. Storage is off either way.
        var options = new ChatOptions();
        if (isReasoningEnabled is not null)
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatRequestProperties.IsReasoningEnabled] = isReasoningEnabled.Value
            };
        }

        var raw = RawOptions(Apply(Settings(), options));

        Assert.Null(raw.ReasoningOptions);
        Assert.False(raw.StoredOutputEnabled);
    }

    [Fact]
    public void Create_WhenTheCallerSuppliedAFactory_KeepsIt()
    {
        var supplied = new ChatOptions
        {
            RawRepresentationFactory = _ => null,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatRequestProperties.IsReasoningEnabled] = true
            }
        };
        var factory = supplied.RawRepresentationFactory;

        Assert.Same(factory, Apply(Settings(), supplied).RawRepresentationFactory);
    }

    [Theory]
    [MemberData(nameof(ReasoningSummaries))]
    public void Create_ForEverySummaryTheValidatorAccepts_Parses(string summary)
    {
        var settings = Settings();
        settings.ReasoningSummary = summary;

        // Parsing happens once in Create, so an unknown value throws here, not on the first conversation.
        Assert.NotNull(AzureOpenAIDefaults.Create(settings));
    }

    [Theory]
    [MemberData(nameof(ReasoningEfforts))]
    public void Create_ForEveryEffortTheValidatorAccepts_Parses(string effort)
    {
        var settings = Settings();
        settings.ReasoningEffort = effort;

        Assert.NotNull(AzureOpenAIDefaults.Create(settings));
    }

    [Fact]
    public void Create_WithAnUnknownEffort_ThrowsRatherThanGuessing()
    {
        var settings = Settings();
        settings.ReasoningEffort = "extreme";

        Assert.Throws<ArgumentOutOfRangeException>(() => AzureOpenAIDefaults.Create(settings));
    }

    [Fact]
    public void Create_WithAnUnknownSummary_ThrowsRatherThanGuessing()
    {
        var settings = Settings();
        settings.ReasoningSummary = "verbose";

        Assert.Throws<ArgumentOutOfRangeException>(() => AzureOpenAIDefaults.Create(settings));
    }

    public static TheoryData<string> ReasoningSummaries() =>
        [.. AzureOpenAIOptions.ReasoningSummaries];

    public static TheoryData<string> ReasoningEfforts() =>
        [.. AzureOpenAIOptions.ReasoningEfforts];
}
