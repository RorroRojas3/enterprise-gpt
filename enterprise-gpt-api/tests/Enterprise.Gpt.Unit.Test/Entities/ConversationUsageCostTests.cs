using Enterprise.Gpt.Entity;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Entities;

public sealed class ConversationUsageCostTests
{
    private static ConversationUsage CreateUsage(decimal? inputPrice, decimal? outputPrice)
    {
        return new ConversationUsage
        {
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            ToolInputTokens = 200_000,
            ToolOutputTokens = 100_000,
            InputPricePerMillionTokens = inputPrice,
            OutputPricePerMillionTokens = outputPrice
        };
    }

    [Fact]
    public void EstimatedCost_BothPricesSet_PricesModelAndToolTokensTogether()
    {
        var usage = CreateUsage(2m, 10m);

        // (1,000,000 + 200,000) input at 2.00/M = 2.40; (500,000 + 100,000) output at 10.00/M = 6.00.
        Assert.Equal(8.40m, usage.EstimatedCost);
    }

    [Theory]
    [InlineData(null, 10d)]
    [InlineData(2d, null)]
    [InlineData(null, null)]
    public void EstimatedCost_EitherPriceMissing_IsNullRatherThanPartial(double? inputPrice, double? outputPrice)
    {
        var usage = CreateUsage((decimal?)inputPrice, (decimal?)outputPrice);

        Assert.Null(usage.EstimatedCost);
    }

    [Fact]
    public void EstimatedCost_ZeroPrices_IsZeroRatherThanNull()
    {
        // A genuinely free deployment is distinguishable from an unpriced one, which is the whole
        // reason the columns are nullable instead of defaulted.
        var usage = CreateUsage(0m, 0m);

        Assert.Equal(0m, usage.EstimatedCost);
    }

    [Fact]
    public void EstimatedCost_IgnoresContextTokens()
    {
        var withoutContext = CreateUsage(2m, 10m);
        var withContext = CreateUsage(2m, 10m);
        withContext.ContextTokens = 9_999_999;

        // Cost is what the provider reported it billed, never what the transcript weighs. The two
        // sit on the same row and must never be added.
        Assert.Equal(withoutContext.EstimatedCost, withContext.EstimatedCost);
    }

    [Fact]
    public void ContextTokens_DefaultsToNull()
    {
        // Null means "not transcribed", which is the state of every cancelled or failed turn.
        // Zero would claim the turn was transcribed and weighed nothing.
        Assert.Null(new ConversationUsage().ContextTokens);
    }
}
