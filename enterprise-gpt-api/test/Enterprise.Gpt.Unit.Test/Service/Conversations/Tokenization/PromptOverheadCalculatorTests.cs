using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tokenization;

public sealed class PromptOverheadCalculatorTests
{
    private const int Framing = 4;
    private const int PerTool = 120;

    private static readonly PromptOverheadCalculator Calculator = new(
        Options.Create(new TokenEstimationOptions
        {
            PerMessageFramingTokens = Framing,
            PerToolSchemaTokens = PerTool
        }));

    [Fact]
    public void Estimate_NoToolsAndNoInstructions_ContributesOnlyTheFramingConstants()
    {
        Assert.Equal(10 * Framing, Calculator.Estimate(messageCount: 10, toolCount: 0, instructionTokens: 0));
    }

    [Fact]
    public void Estimate_FramingIsAppliedOncePerMessage()
    {
        var one = Calculator.Estimate(messageCount: 1, toolCount: 0, instructionTokens: 0);
        var ten = Calculator.Estimate(messageCount: 10, toolCount: 0, instructionTokens: 0);

        Assert.Equal(one * 10, ten);
    }

    [Fact]
    public void Estimate_ToolsAttached_AddsThePerToolSchemaCost()
    {
        var withoutTools = Calculator.Estimate(messageCount: 5, toolCount: 0, instructionTokens: 0);
        var withTools = Calculator.Estimate(messageCount: 5, toolCount: 3, instructionTokens: 0);

        Assert.Equal(withoutTools + (3 * PerTool), withTools);
    }

    [Fact]
    public void Estimate_Instructions_AreCountedIntoTheOverheadRatherThanAnyMessage()
    {
        var withoutInstructions = Calculator.Estimate(messageCount: 5, toolCount: 0, instructionTokens: 0);
        var withInstructions = Calculator.Estimate(messageCount: 5, toolCount: 0, instructionTokens: 42);

        Assert.Equal(withoutInstructions + 42, withInstructions);
    }

    [Fact]
    public void Estimate_NegativeCounts_AreTreatedAsZero()
    {
        Assert.Equal(0, Calculator.Estimate(messageCount: -1, toolCount: -1, instructionTokens: -1));
    }
}
