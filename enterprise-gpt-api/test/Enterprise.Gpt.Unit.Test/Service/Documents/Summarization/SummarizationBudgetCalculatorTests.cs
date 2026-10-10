using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Summarization;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

public sealed class SummarizationBudgetCalculatorTests
{
    private static readonly Guid ModelId = Guid.Parse("c36e22ed-262a-47a1-b2ba-06a38355ae0f");

    private static SummarizerModel CreateSummarizer(
        decimal contextWindowSize = 1_000_000m, decimal maxOutputTokens = 16_384m)
    {
        return new SummarizerModel(
            ModelId, Providers.AzureOpenAI, "rr-gpt5.6-luna", contextWindowSize, maxOutputTokens, null, null);
    }

    /// <summary>
    /// The invariant the whole feature rests on. A window of zero or less is the seeded default for
    /// a row nobody filled in, and the shared context-budget calculator reads exactly that value as
    /// unbounded. Inheriting that reading here would send a multi-million-token document in one
    /// call, so this test exists independently of any that happens to run against the corrected seed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1_000_000)]
    public void Compute_UnconfiguredContextWindow_Refuses(decimal contextWindowSize)
    {
        var exception = Assert.Throws<SummarizerNotConfiguredException>(
            () => SummarizationBudgetCalculator.Compute(
                CreateSummarizer(contextWindowSize, maxOutputTokens: 0m), 0, 0.85, 0.9));

        Assert.Equal(ModelId, exception.ModelId);
        Assert.Contains("context window", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_ConfiguredContextWindow_ReturnsABudget()
    {
        var budget = SummarizationBudgetCalculator.Compute(CreateSummarizer(), 0, 0.85, 0.9);

        Assert.True(budget.PerCallTokens > 0);
        Assert.True(budget.MapUnitTokens > 0);
    }

    /// <summary>
    /// A window that cannot hold its own output reservation is as unconfigured as one of zero: the
    /// subtraction would otherwise hand back a negative budget that reads as "nothing fits".
    /// </summary>
    [Fact]
    public void Compute_OutputReservationExceedsTheWindow_Refuses()
    {
        var exception = Assert.Throws<SummarizerNotConfiguredException>(
            () => SummarizationBudgetCalculator.Compute(
                CreateSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 4_000m), 0, 0.85, 0.9));

        Assert.Equal(ModelId, exception.ModelId);
    }

    [Fact]
    public void Compute_FramingLargerThanWhatTheWindowLeaves_Refuses()
    {
        Assert.Throws<SummarizerNotConfiguredException>(
            () => SummarizationBudgetCalculator.Compute(
                CreateSummarizer(contextWindowSize: 1_000m, maxOutputTokens: 100m),
                promptOverheadTokens: 5_000,
                0.85,
                0.9));
    }

    [Fact]
    public void Compute_OutputReservationAndFraming_AreBothSubtracted()
    {
        var budget = SummarizationBudgetCalculator.Compute(
            CreateSummarizer(contextWindowSize: 10_000m, maxOutputTokens: 2_000m),
            promptOverheadTokens: 1_000,
            safetyFraction: 1.0,
            mapUnitFraction: 1.0);

        Assert.Equal(7_000, budget.PerCallTokens);
    }

    /// <summary>
    /// A multiplier, never an additive margin: a fixed token margin correct for a 128k window is
    /// meaningless against a 1,000,000-token one.
    /// </summary>
    [Theory]
    [InlineData(1.0, 100_000)]
    [InlineData(0.85, 85_000)]
    [InlineData(0.5, 50_000)]
    public void Compute_SafetyFraction_ScalesTheBudget(double safetyFraction, long expected)
    {
        var budget = SummarizationBudgetCalculator.Compute(
            CreateSummarizer(contextWindowSize: 100_000m, maxOutputTokens: 0m),
            0,
            safetyFraction,
            mapUnitFraction: 1.0);

        Assert.Equal(expected, budget.PerCallTokens);
    }

    [Fact]
    public void Compute_MapUnitFraction_LeavesHeadroomUnderThePerCallBudget()
    {
        var budget = SummarizationBudgetCalculator.Compute(
            CreateSummarizer(contextWindowSize: 100_000m, maxOutputTokens: 0m), 0, 1.0, 0.9);

        Assert.Equal(100_000, budget.PerCallTokens);
        Assert.Equal(90_000, budget.MapUnitTokens);
    }

    /// <summary>
    /// A window barely larger than its reservation must still yield a usable budget rather than a
    /// zero one, which would make every document look oversized and split forever.
    /// </summary>
    [Fact]
    public void Compute_VerySmallRemainder_StillYieldsAtLeastOneToken()
    {
        var budget = SummarizationBudgetCalculator.Compute(
            CreateSummarizer(contextWindowSize: 101m, maxOutputTokens: 100m), 0, 0.01, 0.01);

        Assert.Equal(1, budget.PerCallTokens);
        Assert.Equal(1, budget.MapUnitTokens);
    }

    [Fact]
    public void Compute_NullSummarizer_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SummarizationBudgetCalculator.Compute(null!, 0, 0.85, 0.9));
    }
}
