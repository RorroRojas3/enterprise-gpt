using Enterprise.Gpt.Service.Settings;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Settings;

/// <summary>
/// Covers the accepted-value sets and the cross-field rule the startup validators in
/// <c>Program.cs</c> read from, so the one non-obvious configuration constraint is pinned without
/// booting a host.
/// </summary>
public sealed class AnthropicOptionsTests
{
    // FrozenSet implements both ISet and IReadOnlySet, which makes Assert.Contains ambiguous against
    // it. Narrowing to one of them picks an overload that still compares through the set's own
    // case-insensitive comparer, which is what the startup validators rely on.
    private static IReadOnlySet<string> ThinkingModes => AnthropicOptions.ThinkingModes;

    private static IReadOnlySet<string> EffortLevels => AnthropicOptions.EffortLevels;

    [Theory]
    [InlineData("adaptive")]
    [InlineData("disabled")]
    [InlineData("Adaptive")]
    public void ThinkingModes_AcceptedValue_IsRecognizedRegardlessOfCasing(string thinking)
    {
        Assert.Contains(thinking, ThinkingModes);
    }

    /// <summary>
    /// A fixed thinking budget is rejected by every model this provider targets; <c>Effort</c> is
    /// what replaced it. Naming it here stops it being reintroduced as a third mode.
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
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
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
    [InlineData("disabled", "low")]
    [InlineData("disabled", "medium")]
    [InlineData("disabled", "high")]
    [InlineData("adaptive", "xhigh")]
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
