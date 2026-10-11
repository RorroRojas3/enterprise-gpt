using Enterprise.Gpt.Service.Reports;
using Enterprise.Gpt.Service.Reports.Models;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Reports;

/// <summary>
/// The one place null and zero are told apart, pinned without a database.
/// </summary>
/// <remarks>
/// Getting it wrong reads an unpriceable period as free. EF coalesces every <c>SUM</c> to zero, so
/// <c>AggregateRow.EstimatedCost</c> restores null from the unpriced count.
/// </remarks>
public sealed class UsageCostFoldTests
{
    private static AggregateRow<int> Row(long turns, long unpriced, decimal? pricedCost) =>
        new() { Key = 0, Turns = turns, UnpricedTurns = unpriced, PricedCost = pricedCost };

    [Fact]
    public void EstimatedCost_NothingPriced_IsNullRatherThanTheCoalescedZero()
    {
        Assert.Null(Row(turns: 3, unpriced: 3, pricedCost: 0m).EstimatedCost);
    }

    [Fact]
    public void EstimatedCost_EverythingPricedAtZero_IsZeroRatherThanNull()
    {
        // Free is a price; null would claim the cost is unknown.
        Assert.Equal(0m, Row(turns: 3, unpriced: 0, pricedCost: 0m).EstimatedCost);
    }

    [Fact]
    public void EstimatedCost_PartlyPriced_ReportsWhatCouldBePriced()
    {
        Assert.Equal(4.5m, Row(turns: 4, unpriced: 1, pricedCost: 4.5m).EstimatedCost);
    }

    [Fact]
    public void EstimatedCost_NoTurnsAtAll_IsNull()
    {
        // An empty period should read as unpriced, not as a measured zero.
        Assert.Null(Row(turns: 0, unpriced: 0, pricedCost: 0m).EstimatedCost);
    }

    [Fact]
    public void FoldCost_NoPricedGroups_IsNull()
    {
        Assert.Null(ReportService.FoldCost([Row(2, 2, 0m), Row(1, 1, 0m)]));
    }

    [Fact]
    public void FoldCost_SomePricedGroups_AddsOnlyThose()
    {
        Assert.Equal(7m, ReportService.FoldCost([Row(2, 2, 0m), Row(1, 0, 3m), Row(1, 0, 4m)]));
    }

    [Fact]
    public void FoldCost_EveryGroupFree_IsZeroRatherThanNull()
    {
        Assert.Equal(0m, ReportService.FoldCost([Row(1, 0, 0m), Row(1, 0, 0m)]));
    }

    [Fact]
    public void FoldCost_NoGroups_IsNull()
    {
        Assert.Null(ReportService.FoldCost<int>([]));
    }
}
