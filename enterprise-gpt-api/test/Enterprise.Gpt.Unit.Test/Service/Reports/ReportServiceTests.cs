using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Reports;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Reports;

/// <summary>
/// The refusals <c>ReportService</c> makes before it touches the database.
/// </summary>
/// <remarks>
/// Aggregation is covered by <c>ReportEndpointsIntegrationTests</c> on SQL Server: SQLite cannot
/// translate day-bucketing of a <see cref="DateTimeOffset"/>, so the usage-over-time query fails here.
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
            _fixture.Context, Microsoft.Extensions.Options.Options.Create(_options), NullLogger<ReportService>.Instance, TimeProvider.System);
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

        // An accepted window reaches SQLite, which cannot run the day-bucketing query, so assert
        // only that the window was not refused.
        var thrown = await Record.ExceptionAsync(() =>
            _service.GetUsageReportAsync(
                Anchor.AddDays(-7), Anchor, null, TestContext.Current.CancellationToken));

        // Not asserting a throw too: that would fail the day SQLite learns to translate the query.
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
