using FluentValidation;
using Enterprise.Gpt.Service.Reports;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Reports;

/// <summary>
/// Keeps the three copies of the grouping table in step — the enumeration, the display list and
/// the lookup. Without this, the set a rejection advertises and the set actually accepted drift.
/// </summary>
public sealed class UsageGroupKeysTests
{
    [Fact]
    public void Supported_ListsEveryEnumeratedGroupingExactlyOnce()
    {
        var resolved = UsageGroupKeys.Supported.Select(UsageGroupKeys.Resolve).ToArray();

        Assert.Equal(Enum.GetValues<UsageGroupKey>().Length, UsageGroupKeys.Supported.Count);
        Assert.Equal(Enum.GetValues<UsageGroupKey>().Order(), resolved.Order());
        Assert.Equal(UsageGroupKeys.Supported.Count, UsageGroupKeys.Supported.Distinct().Count());
    }

    [Fact]
    public void ToToken_RoundTripsEveryGrouping()
    {
        foreach (var key in Enum.GetValues<UsageGroupKey>())
        {
            Assert.Equal(key, UsageGroupKeys.Resolve(UsageGroupKeys.ToToken(key)));
        }
    }

    [Fact]
    public void SupportedList_QuotesEveryAcceptedToken()
    {
        foreach (var token in UsageGroupKeys.Supported)
        {
            Assert.Contains($"'{token}'", UsageGroupKeys.SupportedList);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoValue_FallsBackToTheDefault(string? groupBy)
    {
        Assert.Equal(UsageGroupKeys.Default, UsageGroupKeys.Resolve(groupBy));
    }

    [Theory]
    [InlineData("MODEL")]
    [InlineData("Provider")]
    [InlineData("  user  ")]
    public void Resolve_IsCaseInsensitiveAndTrims(string groupBy)
    {
        // Trimmed and case-folded on purpose: a table built with the default comparer would reject
        // '?groupBy=MODEL' with a message listing 'model' as supported.
        Assert.Contains(UsageGroupKeys.Resolve(groupBy), Enum.GetValues<UsageGroupKey>());
    }

    [Fact]
    public void Resolve_UnknownValue_IsRefusedUnderTheQueryParameterName()
    {
        var failure = Assert.Throws<ValidationException>(() => UsageGroupKeys.Resolve("conversation"));
        var error = Assert.Single(failure.Errors);

        Assert.Equal(UsageGroupKeys.GroupByKey, error.PropertyName);
        Assert.Contains("conversation", error.ErrorMessage);
        Assert.Contains(UsageGroupKeys.SupportedList, error.ErrorMessage);
    }
}
