using Enterprise.Gpt.Api.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Options;

public sealed class AnthropicOptionsTests
{
    // FrozenSet is both ISet and IReadOnlySet, making Assert.Contains ambiguous; narrowing keeps the
    // set's case-insensitive comparer, which the startup validators rely on.
    private static IReadOnlySet<string> ThinkingModes => AnthropicOptions.ThinkingModes;

    private static IReadOnlySet<string> EffortLevels => AnthropicOptions.EffortLevels;

    [Theory]
    [InlineData("Adaptive")]
    public void ThinkingModes_AcceptedValue_IsRecognizedRegardlessOfCasing(string thinking)
    {
        Assert.Contains(thinking, ThinkingModes);
    }

    /// <summary>
    /// Every targeted model rejects a fixed thinking budget; naming it stops it returning as a third mode.
    /// </summary>
    [Theory]
    [InlineData("enabled")]
    [InlineData("budget_tokens")]
    [InlineData("")]
    public void ThinkingModes_UnsupportedValue_IsRejected(string thinking)
    {
        Assert.DoesNotContain(thinking, ThinkingModes);
    }

    [Theory]
    [InlineData("HIGH")]
    public void EffortLevels_AcceptedValue_IsRecognizedRegardlessOfCasing(string effort)
    {
        Assert.Contains(effort, EffortLevels);
    }

    [Theory]
    [InlineData("extreme")]
    [InlineData("x-high")]
    [InlineData("")]
    public void EffortLevels_UnsupportedValue_IsRejected(string effort)
    {
        Assert.DoesNotContain(effort, EffortLevels);
    }

    /// <summary>
    /// Disabled thinking above <c>high</c> effort is rejected by the model with a 400 on every
    /// request, so it has to fail at startup instead.
    /// </summary>
    [Theory]
    [InlineData("disabled", "xhigh")]
    [InlineData("disabled", "max")]
    [InlineData("Disabled", "MAX")]
    public void IsThinkingEffortCombinationValid_DisabledThinkingAboveHighEffort_IsFalse(
        string thinking,
        string effort)
    {
        var options = new AnthropicOptions { Thinking = thinking, Effort = effort };

        Assert.False(options.IsThinkingEffortCombinationValid);
    }

    [Theory]
    [InlineData("disabled", "high")]
    [InlineData("adaptive", "max")]
    public void IsThinkingEffortCombinationValid_SupportedPairing_IsTrue(string thinking, string effort)
    {
        var options = new AnthropicOptions { Thinking = thinking, Effort = effort };

        Assert.True(options.IsThinkingEffortCombinationValid);
    }

    /// <summary>
    /// The defaults are what an operator gets by turning the provider on and setting nothing else, so
    /// they have to be a combination the validators accept.
    /// </summary>
    [Fact]
    public void Defaults_AreAcceptedByEveryValidator()
    {
        var options = new AnthropicOptions();

        Assert.Contains(options.Thinking, ThinkingModes);
        Assert.Contains(options.Effort, EffortLevels);
        Assert.True(options.IsThinkingEffortCombinationValid);
        Assert.False(options.Enabled);
    }
}
