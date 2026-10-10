using Enterprise.Gpt.Api.Chat;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Xunit;

// OPENAI001: the Responses request types are still marked experimental at OpenAI 2.12.0, and remain
// so at 2.13.0 — no package bump retires this.
#pragma warning disable OPENAI001

namespace Enterprise.Gpt.Unit.Test.Chat;

/// <summary>
/// Covers the callback that stamps Responses settings onto an Azure OpenAI turn, in isolation
/// from the SDK bridge. <see cref="AzureOpenAIChatClientBridgeTests"/> covers what actually reaches
/// the wire; this covers the decisions the callback makes before it gets there.
/// </summary>
public sealed class AzureOpenAIChatDefaultsTests
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
        AzureOpenAIChatDefaults.Create(settings)(options);

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
        // Null covers a request assembled somewhere that never set the property. Absent means no:
        // a deployment that does not support reasoning rejects the whole request rather than
        // ignoring the option, so guessing yes would break every turn on it. Storage is off either
        // way — that is about the turn, not the model.
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

        // Parsing happens once inside Create, so an unknown value throws here rather than on the
        // first conversation. This theory is what keeps the option vocabulary and the parser from
        // drifting apart.
        Assert.NotNull(AzureOpenAIChatDefaults.Create(settings));
    }

    [Theory]
    [MemberData(nameof(ReasoningEfforts))]
    public void Create_ForEveryEffortTheValidatorAccepts_Parses(string effort)
    {
        var settings = Settings();
        settings.ReasoningEffort = effort;

        Assert.NotNull(AzureOpenAIChatDefaults.Create(settings));
    }

    [Fact]
    public void Create_WithAnUnknownEffort_ThrowsRatherThanGuessing()
    {
        var settings = Settings();
        settings.ReasoningEffort = "extreme";

        Assert.Throws<ArgumentOutOfRangeException>(() => AzureOpenAIChatDefaults.Create(settings));
    }

    [Fact]
    public void Create_WithAnUnknownSummary_ThrowsRatherThanGuessing()
    {
        var settings = Settings();
        settings.ReasoningSummary = "verbose";

        Assert.Throws<ArgumentOutOfRangeException>(() => AzureOpenAIChatDefaults.Create(settings));
    }

    public static TheoryData<string> ReasoningSummaries() =>
        [.. AzureOpenAIOptions.ReasoningSummaries];

    public static TheoryData<string> ReasoningEfforts() =>
        [.. AzureOpenAIOptions.ReasoningEfforts];
}
