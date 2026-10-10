using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Service.Reports;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

/// <summary>
/// The refusals <c>ReportService</c> makes before it touches the database.
/// </summary>
/// <remarks>
/// Only the refusals. Everything the service aggregates is verified in
/// <c>ReportEndpointsIntegrationTests</c> against SQL Server instead, because SQLite cannot bucket
/// a <see cref="DateTimeOffset"/> by day at all — no member of it is translated by that provider,
/// so the query behind the usage-over-time series does not compile there. Verifying the arithmetic
/// on a provider that cannot run its central query would prove less than it appears to, and the
/// alternative — a denormalized UTC date column on the largest, fastest-growing table in the
/// schema, plus a backfill that <c>Database.Migrate()</c> would run at startup — is a much larger
/// change to make a test host work.
/// <para>
/// These cases belong here rather than there because every one of them throws before a query is
/// built, which is itself the thing worth pinning: a misspelled grouping costs a round trip to
/// nothing rather than a report the caller then has to distrust.
/// </para>
/// </remarks>
public sealed class ReportServiceTests : IDisposable
{
    private static readonly DateTimeOffset Anchor = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ReportOptions _options = new();
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        _service = new ReportService(
            _fixture.Context, Options.Create(_options), NullLogger<ReportService>.Instance);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task GetUsageReportAsync_FromAfterTo_ThrowsValidationKeyedByTheParameterSent()
    {
        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.GetUsageReportAsync(Anchor, Anchor.AddDays(-1), null, TestContext.Current.CancellationToken));

        Assert.Equal("from", Assert.Single(failure.Errors).PropertyName);
    }

    [Fact]
    public async Task GetUsageReportAsync_FromEqualToTo_IsRefusedRatherThanReportedAsEmpty()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.GetUsageReportAsync(Anchor, Anchor, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUsageReportAsync_RangeBeyondTheConfiguredMaximum_IsRefusedNotClamped()
    {
        _options.MaxRangeDays = 7;

        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.GetUsageReportAsync(
                Anchor.AddDays(-8), Anchor, null, TestContext.Current.CancellationToken));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("from", error.PropertyName);
        Assert.Contains("7 days", error.ErrorMessage);
    }

    [Fact]
    public async Task GetUsageReportAsync_RangeExactlyTheConfiguredMaximum_IsAccepted()
    {
        _options.MaxRangeDays = 7;

        // The window is accepted, so execution reaches the database — where SQLite fails on the
        // day-bucketing query. Asserting the *absence* of a validation failure is what makes this
        // about the boundary rather than about the provider: a range exactly at the maximum is
        // inside it, and an off-by-one here would silently shorten every deployment's longest
        // report by a day.
        var thrown = await Record.ExceptionAsync(() =>
            _service.GetUsageReportAsync(
                Anchor.AddDays(-7), Anchor, null, TestContext.Current.CancellationToken));

        // Only that it was not *refused*. Whether anything else went wrong afterwards is this
        // provider's business, not the boundary's — asserting a throw as well would make the test
        // fail for an unrelated reason the day SQLite learns to translate the query.
        Assert.IsNotType<ValidationException>(thrown);
    }

    [Fact]
    public async Task GetUsageReportAsync_UnrecognisedGrouping_ThrowsValidationKeyedByGroupBy()
    {
        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.GetUsageReportAsync(
                Anchor.AddDays(-1), Anchor, "conversation", TestContext.Current.CancellationToken));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("groupBy", error.PropertyName);
        Assert.Contains("'model'", error.ErrorMessage);
    }

    [Fact]
    public async Task GetUsageReportAsync_GroupingIsRejectedBeforeTheWindow_SoOneCallReportsOneFault()
    {
        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.GetUsageReportAsync(Anchor, Anchor.AddDays(-1), "nonsense", TestContext.Current.CancellationToken));

        Assert.Equal("groupBy", Assert.Single(failure.Errors).PropertyName);
    }
}
