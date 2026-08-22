using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Reports;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class ReportEndpointsTests
{
    private readonly IReportService _reportService = Substitute.For<IReportService>();

    [Fact]
    public async Task GetUsageReportAsync_ServiceReturnsReport_ReturnsOkWithPayload()
    {
        var expected = new UsageReportDto
        {
            GroupBy = "model",
            Totals = new UsageReportTotalsDto(),
            PriorTotals = new UsageReportTotalsDto()
        };
        _reportService
            .GetUsageReportAsync(
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await ReportEndpoints.GetUsageReportAsync(
            _reportService, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetUsageReportAsync_NoQueryString_PassesTheAbsencesThroughRatherThanDefaultingThem()
    {
        // The handler must not invent a window. Both bounds and the grouping are resolved in the
        // service, which is also where they are validated — a default applied here would be a
        // second copy of that decision, and the two would drift.
        _reportService
            .GetUsageReportAsync(null, null, null, Arg.Any<CancellationToken>())
            .Returns(new UsageReportDto
            {
                GroupBy = "model",
                Totals = new UsageReportTotalsDto(),
                PriorTotals = new UsageReportTotalsDto()
            });

        await ReportEndpoints.GetUsageReportAsync(
            _reportService, cancellationToken: TestContext.Current.CancellationToken);

        await _reportService.Received(1)
            .GetUsageReportAsync(null, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUsageReportAsync_QueryStringSupplied_FlowsEveryParameterToTheService()
    {
        var from = new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
        _reportService
            .GetUsageReportAsync(from, to, "user", Arg.Any<CancellationToken>())
            .Returns(new UsageReportDto
            {
                GroupBy = "user",
                Totals = new UsageReportTotalsDto(),
                PriorTotals = new UsageReportTotalsDto()
            });

        var result = await ReportEndpoints.GetUsageReportAsync(
            _reportService, from, to, "user", TestContext.Current.CancellationToken);

        Assert.Equal("user", result.Value!.GroupBy);
        await _reportService.Received(1)
            .GetUsageReportAsync(from, to, "user", Arg.Any<CancellationToken>());
    }
}
