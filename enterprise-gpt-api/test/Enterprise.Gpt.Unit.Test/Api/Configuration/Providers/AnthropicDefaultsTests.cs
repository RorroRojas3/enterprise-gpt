using Anthropic.Models.Messages;
using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Api.Options;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Configuration.Providers;

public sealed class AnthropicDefaultsTests
{
    /// <summary>
    /// The chat client is built lazily, so a mode in <see cref="AnthropicOptions.ThinkingModes"/> without a
    /// parser arm would otherwise fail on the first conversation rather than here.
    /// </summary>
    public static TheoryData<string> AcceptedThinkingModes => [.. AnthropicOptions.ThinkingModes];

    /// <inheritdoc cref="AcceptedThinkingModes" />
    public static TheoryData<string> AcceptedEffortLevels => [.. AnthropicOptions.EffortLevels];

    private static AnthropicOptions Settings(string thinking = "adaptive", string effort = "high") =>
        new()
        {
            Enabled = true,
            ApiKey = "test-key",
            DefaultModelId = "claude-default",
            DefaultMaxOutputTokens = 4096,
            Thinking = thinking,
            Effort = effort
        };

    private static MessageCreateParams Apply(AnthropicOptions settings, ChatOptions options)
    {
        AnthropicDefaults.Create(settings)(options);

        return Assert.IsType<MessageCreateParams>(options.RawRepresentationFactory!.Invoke(null!));
    }

    /// <summary>
    /// The bridge leaves <c>Model</c> and <c>MaxTokens</c> as supplied, so reading them off the request is
    /// what routes each model row to its own deployment.
    /// </summary>
    [Fact]
    public void Create_RequestCarriesModelAndMaxTokens_UsesTheRequestValues()
    {
        var options = new ChatOptions { ModelId = "claude-from-catalog", MaxOutputTokens = 1234 };

        var parameters = Apply(Settings(), options);

        Assert.Equal("claude-from-catalog", parameters.Model.Raw());
        Assert.Equal(1234, parameters.MaxTokens);
    }

    [Fact]
    public void Create_RequestCarriesNeitherModelNorMaxTokens_FallsBackToTheConfiguredDefaults()
    {
        var parameters = Apply(Settings(), new ChatOptions());

        Assert.Equal("claude-default", parameters.Model.Raw());
        Assert.Equal(4096, parameters.MaxTokens);
    }

    /// <summary>
    /// A blank deployment name would otherwise reach the wire as an empty model id and 400.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    public void Create_RequestCarriesABlankModelId_FallsBackToTheConfiguredDefault(string modelId)
    {
        var parameters = Apply(Settings(), new ChatOptions { ModelId = modelId });

        Assert.Equal("claude-default", parameters.Model.Raw());
    }

    /// <summary>
    /// The bridge concatenates this list ahead of the conversation it builds from
    /// <see cref="ChatOptions"/>, so anything here would be prepended to every single turn.
    /// </summary>
    [Fact]
    public void Create_Always_LeavesTheMessageListEmpty()
    {
        var parameters = Apply(Settings(), new ChatOptions { ModelId = "claude-from-catalog" });

        Assert.Empty(parameters.Messages);
    }

    [Fact]
    public void Create_AdaptiveThinking_IsSentAsAnAdaptiveConfig()
    {
        var parameters = Apply(Settings(thinking: "adaptive"), new ChatOptions());

        Assert.True(parameters.Thinking!.TryPickAdaptive(out _));
    }

    [Fact]
    public void Create_DisabledThinking_IsSentAsADisabledConfig()
    {
        var parameters = Apply(Settings(thinking: "disabled", effort: "high"), new ChatOptions());

        Assert.True(parameters.Thinking!.TryPickDisabled(out _));
    }

    [Theory]
    [MemberData(nameof(AcceptedThinkingModes))]
    public void Create_EveryThinkingModeTheValidatorsAccept_Parses(string thinking)
    {
        var effort = string.Equals(thinking, AnthropicOptions.DisabledThinking, StringComparison.OrdinalIgnoreCase)
            ? "high"
            : "max";

        Assert.NotNull(Apply(Settings(thinking, effort), new ChatOptions()).Thinking);
    }

    [Theory]
    [MemberData(nameof(AcceptedEffortLevels))]
    public void Create_EveryEffortLevelTheValidatorsAccept_Parses(string effort)
    {
        Assert.NotNull(Apply(Settings(effort: effort), new ChatOptions()).OutputConfig?.Effort);
    }

    /// <summary>
    /// Asserted against the serialized value rather than the enum member, because the two do not
    /// always spell the same — <c>Effort.Xhigh</c> goes on the wire as <c>xhigh</c>.
    /// </summary>
    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "max")]
    [InlineData("HIGH", "high")]
    public void Create_ConfiguredEffort_IsSentAsTheMatchingLevel(string configured, string expected)
    {
        var parameters = Apply(Settings(effort: configured), new ChatOptions());

        Assert.Equal(expected, parameters.OutputConfig?.Effort?.Raw());
    }

    /// <summary>
    /// This provider's models reject sampling parameters with a 400, whatever the thinking mode.
    /// </summary>
    [Theory]
    [InlineData("adaptive", "high")]
    [InlineData("disabled", "high")]
    public void Create_Always_ClearsSamplingParametersTheProviderRejects(string thinking, string effort)
    {
        var options = new ChatOptions { Temperature = 0.7f, TopP = 0.9f, TopK = 40 };

        Apply(Settings(thinking, effort), options);

        Assert.Null(options.Temperature);
        Assert.Null(options.TopP);
        Assert.Null(options.TopK);
    }

    /// <summary>
    /// The stamp is a default, not an override: a call site that needs different parameters for one
    /// turn keeps the escape hatch Microsoft.Extensions.AI provides.
    /// </summary>
    [Fact]
    public void Create_RequestAlreadyCarriesARawRepresentation_LeavesItAlone()
    {
        var callerSupplied = new MessageCreateParams
        {
            Model = "caller-choice",
            MaxTokens = 64,
            Messages = []
        };
        var options = new ChatOptions { RawRepresentationFactory = _ => callerSupplied };

        AnthropicDefaults.Create(Settings())(options);

        Assert.Same(callerSupplied, options.RawRepresentationFactory!.Invoke(null!));
    }
}
