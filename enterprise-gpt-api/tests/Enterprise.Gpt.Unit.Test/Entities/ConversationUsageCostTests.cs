using Enterprise.Gpt.Entity;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Entities;

public class ConversationUsageCostTests
{
    private static ConversationUsage CreateUsage(decimal? inputPrice, decimal? outputPrice)
    {
        return new ConversationUsage
        {
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            ToolInputTokens = 0,
            ToolOutputTokens = 0,
            InputPricePerMillionTokens = inputPrice,
            OutputPricePerMillionTokens = outputPrice
        };
    }

    [Fact]
    public void Cost_BothPricesSet_PricesInputAndOutputSeparately()
    {
        var usage = CreateUsage(2m, 10m);

        Assert.Equal(7m, usage.Cost);
    }

    [Fact]
    public void Cost_ToolTokensPresent_PricesThemAtTheDrivingModelsRate()
    {
        var usage = CreateUsage(2m, 10m);
        usage.ToolInputTokens = 1_000_000;
        usage.ToolOutputTokens = 500_000;

        Assert.Equal(14m, usage.Cost);
    }

    /// <summary>
    /// A missing price means unpriced, so no figure is produced at all — reporting half the cost
    /// as if it were the whole cost is worse than reporting none.
    /// </summary>
    [Theory]
    [InlineData(null, 10d)]
    [InlineData(2d, null)]
    [InlineData(null, null)]
    public void Cost_EitherPriceMissing_ReturnsNull(double? inputPrice, double? outputPrice)
    {
        var usage = CreateUsage((decimal?)inputPrice, (decimal?)outputPrice);

        Assert.Null(usage.Cost);
    }

    /// <summary>
    /// Zero is a price, not an absence: a free model reports a cost of zero rather than nothing.
    /// </summary>
    [Fact]
    public void Cost_PricesAreZero_ReturnsZeroRatherThanNull()
    {
        var usage = CreateUsage(0m, 0m);

        Assert.Equal(0m, usage.Cost);
    }

    [Fact]
    public void Cost_FractionalPrices_DoesNotLosePrecisionToRounding()
    {
        var usage = CreateUsage(0.15m, 0.6m);
        usage.InputTokens = 1_234;
        usage.OutputTokens = 5_678;
        usage.ToolInputTokens = 0;
        usage.ToolOutputTokens = 0;

        Assert.Equal(0.00018510m + 0.00340680m, usage.Cost);
    }
}
