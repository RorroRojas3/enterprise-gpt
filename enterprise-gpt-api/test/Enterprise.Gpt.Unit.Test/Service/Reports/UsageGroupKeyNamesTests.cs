using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Reports;
using FluentValidation;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Reports;

/// <summary>
/// Keeps the three copies of the grouping table in step — the enumeration, the display list and
/// the lookup. Without this, the set a rejection advertises and the set actually accepted drift.
/// </summary>
public sealed class UsageGroupKeyNamesTests
{
    [Fact]
    public void Supported_ListsEveryEnumeratedGroupingExactlyOnce()
    {
        var resolved = UsageGroupKeyNames.Supported.Select(UsageGroupKeyNames.Resolve).ToArray();

        Assert.Equal(Enum.GetValues<UsageGroupKeys>().Length, UsageGroupKeyNames.Supported.Count);
        Assert.Equal(Enum.GetValues<UsageGroupKeys>().Order(), resolved.Order());
        Assert.Equal(UsageGroupKeyNames.Supported.Count, UsageGroupKeyNames.Supported.Distinct().Count());
    }

    [Fact]
    public void ToToken_RoundTripsEveryGrouping()
    {
        foreach (var key in Enum.GetValues<UsageGroupKeys>())
        {
            Assert.Equal(key, UsageGroupKeyNames.Resolve(UsageGroupKeyNames.ToToken(key)));
        }
    }

    [Fact]
    public void SupportedList_QuotesEveryAcceptedToken()
    {
        foreach (var token in UsageGroupKeyNames.Supported)
        {
            Assert.Contains($"'{token}'", UsageGroupKeyNames.SupportedList);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoValue_FallsBackToTheDefault(string? groupBy)
    {
        Assert.Equal(UsageGroupKeyNames.Default, UsageGroupKeyNames.Resolve(groupBy));
    }

    [Theory]
    [InlineData("MODEL")]
    [InlineData("  user  ")]
    public void Resolve_IsCaseInsensitiveAndTrims(string groupBy)
    {
        // Trimmed and case-folded on purpose: a table built with the default comparer would reject
        // '?groupBy=MODEL' with a message listing 'model' as supported.
        Assert.Contains(UsageGroupKeyNames.Resolve(groupBy), Enum.GetValues<UsageGroupKeys>());
    }

    [Fact]
    public void Resolve_UnknownValue_IsRefusedUnderTheQueryParameterName()
    {
        var failure = Assert.Throws<ValidationException>(() => UsageGroupKeyNames.Resolve("conversation"));
        var error = Assert.Single(failure.Errors);

        Assert.Equal(UsageGroupKeyNames.GroupByKey, error.PropertyName);
        Assert.Contains("conversation", error.ErrorMessage);
        Assert.Contains(UsageGroupKeyNames.SupportedList, error.ErrorMessage);
    }
}
