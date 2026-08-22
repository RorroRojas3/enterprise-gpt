using Enterprise.Gpt.Service.Reports;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Reports;

/// <summary>
/// The one place null and zero are told apart, pinned without a database.
/// </summary>
/// <remarks>
/// This arithmetic is the reason the whole report models cost as nullable, and getting it wrong
/// produces a plausible number rather than a failure: a period nobody could price would read as a
/// period that cost nothing. The aggregate itself cannot express the difference — EF wraps every
/// <c>SUM</c> in a <c>COALESCE</c>, so an all-null sum arrives as zero — which is why
/// <c>AggregateRow.EstimatedCost</c> restores it from the unpriced count and why both halves are
/// worth a test of their own.
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
        // Free is a price. Collapsing this into null would say the cost is unknown when it is
        // known and happens to be nothing.
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
        // An empty group is unpriced by the same test, which is what an empty period should read
        // as: nothing was spent that anyone can vouch for, not a measured zero.
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
