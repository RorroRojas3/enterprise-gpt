using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Reports;
using Enterprise.Gpt.Service.Settings;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

/// <summary>
/// <c>GET api/reports/usage</c> end to end (US-1301).
/// </summary>
/// <remarks>
/// The aggregate is verified here rather than in a unit test because SQLite cannot bucket a
/// <c>DateTimeOffset</c> by day — no member of it is translated by that provider — so the query
/// behind the usage-over-time series does not compile there at all. Everything this suite asserts
/// therefore runs against the engine the report will actually run on, including the decimal cost
/// arithmetic and the day bucketing that motivated the move.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReportEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture = fixture;

    private DateTimeOffset _anchor;
    private Guid _conversationId;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);

        // Midday, so a window that reaches back whole days never straddles a boundary by accident.
        var now = DateTimeOffset.UtcNow;
        _anchor = new DateTimeOffset(now.Year, now.Month, now.Day, 12, 0, 0, TimeSpan.Zero);
        _conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private Task SeedAsync(
        int daysAgo,
        ConversationUsageStatuses status = ConversationUsageStatuses.Completed,
        ConversationUsageKinds kind = ConversationUsageKinds.Chat,
        long input = 10, long output = 5, long toolInput = 0, long toolOutput = 0,
        long? contextTokens = null,
        decimal? inputPrice = null, decimal? outputPrice = null,
        Guid? userId = null) =>
        _fixture.AddConversationUsageAsync(
            _conversationId,
            KnownIds.SeedModelId,
            _anchor.AddDays(-daysAgo),
            userId,
            mcpServerIds: null,
            kind: kind,
            status: status,
            inputTokens: input,
            outputTokens: output,
            toolInputTokens: toolInput,
            toolOutputTokens: toolOutput,
            contextTokens: contextTokens,
            inputPricePerMillionTokens: inputPrice,
            outputPricePerMillionTokens: outputPrice,
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Seeds one row against a model other than the seeded default.</summary>
    private Task SeedForModelAsync(Guid modelId, long input) =>
        _fixture.AddConversationUsageAsync(
            _conversationId,
            modelId,
            _anchor.AddDays(-1),
            inputTokens: input,
            outputTokens: 0,
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task<UsageReportDto> GetAsync(int fromDaysAgo = 5, string? groupBy = null)
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var route = Route(_anchor.AddDays(-fromDaysAgo), _anchor)
            + (groupBy is null ? string.Empty : $"&groupBy={groupBy}");

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<UsageReportDto>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(report);

        return report;
    }

    [Fact]
    public async Task GetUsageReport_RegularUser_ReturnsForbiddenNamingTheGrant()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync("api/reports/usage", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", problem.Detail);
    }

    [Fact]
    public async Task GetUsageReport_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("api/reports/usage", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUsageReport_NoRows_ReturnsZeroTotalsAndADenseSeries()
    {
        var report = await GetAsync();

        Assert.Equal(0, report.Totals.Turns);
        Assert.Null(report.Totals.EstimatedCost);
        Assert.Empty(report.Groups);
        Assert.Empty(report.Outcomes);
        Assert.Equal(6, report.Series.Count);
        Assert.All(report.Series, point => Assert.Equal(0, point.TotalTokens));
    }

    [Fact]
    public async Task GetUsageReport_MixedOutcomes_SplitsTurnsAndTheSplitSumsToTheTotal()
    {
        await SeedAsync(1);
        await SeedAsync(1);
        await SeedAsync(2, ConversationUsageStatuses.Cancelled);
        await SeedAsync(2, ConversationUsageStatuses.Failed);

        var report = await GetAsync();

        Assert.Equal(4, report.Totals.Turns);
        Assert.Equal(2, report.Totals.CompletedTurns);
        Assert.Equal(1, report.Totals.CancelledTurns);
        Assert.Equal(1, report.Totals.FailedTurns);
        Assert.Equal(
            report.Totals.Turns,
            report.Totals.CompletedTurns + report.Totals.CancelledTurns + report.Totals.FailedTurns);
        Assert.Equal(3, report.Outcomes.Count);
        Assert.Equal(report.Totals.Turns, report.Outcomes.Sum(outcome => outcome.Turns));
        Assert.Equal(report.Totals.TotalTokens, report.Outcomes.Sum(outcome => outcome.TotalTokens));
    }

    [Fact]
    public async Task GetUsageReport_NamingCall_CountsAsATurnAndIsAlsoReportedApart()
    {
        await SeedAsync(1);
        await SeedAsync(1, kind: ConversationUsageKinds.Naming, input: 4, output: 1);

        var report = await GetAsync();

        Assert.Equal(2, report.Totals.Turns);
        Assert.Equal(1, report.Totals.NamingTurns);
        Assert.Equal(5, report.Totals.NamingTokens);
        Assert.Equal(20, report.Totals.TotalTokens);
    }

    [Fact]
    public async Task GetUsageReport_ToolTokens_AreCountedApartAndSummedIntoTheTotal()
    {
        await SeedAsync(1, input: 100, output: 20, toolInput: 7, toolOutput: 3);

        var report = await GetAsync();

        Assert.Equal(120, report.Totals.AssistantTokens);
        Assert.Equal(10, report.Totals.ToolTokens);
        Assert.Equal(130, report.Totals.TotalTokens);
    }

    [Fact]
    public async Task GetUsageReport_ContextTokens_SitBesideTheBilledColumnsNotInsideThem()
    {
        await SeedAsync(1, input: 100, output: 20, contextTokens: 4242);

        var report = await GetAsync();

        Assert.Equal(4242, report.Totals.ContextTokens);
        Assert.Equal(120, report.Totals.TotalTokens);
    }

    [Fact]
    public async Task GetUsageReport_UnpricedCalls_LeaveCostNullRatherThanZero()
    {
        await SeedAsync(1);

        var report = await GetAsync();

        Assert.Null(report.Totals.EstimatedCost);
        Assert.Equal(1, report.Totals.UnpricedTurns);
    }

    [Fact]
    public async Task GetUsageReport_PartlyPriced_CostsThePricedCallsAndCountsTheRest()
    {
        // A million input at $2 and a million output at $6 is $8 exactly, which is what makes the
        // per-million divisor assertable rather than approximately right.
        await SeedAsync(1, input: 1_000_000, output: 1_000_000, inputPrice: 2m, outputPrice: 6m);
        await SeedAsync(1);

        var report = await GetAsync();

        Assert.Equal(8m, report.Totals.EstimatedCost);
        Assert.Equal(1, report.Totals.UnpricedTurns);
    }

    [Fact]
    public async Task GetUsageReport_ToolTokens_ArePricedAtTheDrivingModelsRate()
    {
        await SeedAsync(
            1, input: 500_000, output: 0, toolInput: 500_000, toolOutput: 0,
            inputPrice: 2m, outputPrice: 6m);

        var report = await GetAsync();

        Assert.Equal(2m, report.Totals.EstimatedCost);
    }

    [Fact]
    public async Task GetUsageReport_HalfPricedModel_LeavesCostNullBecauseAPartialFigureUnderstates()
    {
        await SeedAsync(1, input: 1_000_000, output: 0, inputPrice: 2m);

        var report = await GetAsync();

        Assert.Null(report.Totals.EstimatedCost);
        Assert.Equal(1, report.Totals.UnpricedTurns);
    }

    [Fact]
    public async Task GetUsageReport_PriorPeriod_CoversTheEqualWindowImmediatelyBefore()
    {
        await SeedAsync(1, input: 10, output: 0);
        await SeedAsync(4, input: 70, output: 0);
        await SeedAsync(9, input: 900, output: 0);

        var report = await GetAsync(fromDaysAgo: 3);

        Assert.Equal(10, report.Totals.TotalTokens);
        Assert.Equal(70, report.PriorTotals.TotalTokens);
    }

    [Fact]
    public async Task GetUsageReport_Window_IsHalfOpenSoConsecutivePeriodsCannotShareARow()
    {
        var boundary = _anchor.AddDays(-1);
        await SeedAsync(1, input: 11, output: 0);

        using var client = _fixture.Factory.CreateAdminClient();
        var earlier = await client.GetFromJsonAsync<UsageReportDto>(
            Route(boundary.AddDays(-1), boundary), TestContext.Current.CancellationToken);
        var later = await client.GetFromJsonAsync<UsageReportDto>(
            Route(boundary, _anchor), TestContext.Current.CancellationToken);

        Assert.Equal(0, earlier!.Totals.Turns);
        Assert.Equal(1, later!.Totals.Turns);
    }

    [Fact]
    public async Task GetUsageReport_DistinctCounts_CountPeopleAndThreadsRatherThanCalls()
    {
        await SeedAsync(1);
        await SeedAsync(1);
        await SeedAsync(1, userId: TestUsers.AdminUserId);

        var report = await GetAsync();

        Assert.Equal(3, report.Totals.Turns);
        Assert.Equal(2, report.Totals.ActiveUsers);
        Assert.Equal(1, report.Totals.Conversations);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("user")]
    [InlineData("day")]
    public async Task GetUsageReport_EveryGrouping_TranslatesAndLabelsItsRows(string groupBy)
    {
        await SeedAsync(1);

        var report = await GetAsync(groupBy: groupBy);

        Assert.Equal(groupBy, report.GroupBy);
        var group = Assert.Single(report.Groups);
        Assert.NotEmpty(group.Key);
        Assert.NotEmpty(group.Label);
        Assert.Equal(1, group.Turns);
        Assert.Equal(15, group.TotalTokens);
    }

    [Fact]
    public async Task GetUsageReport_GroupedByUser_CarriesTheEmailSoNamesCannotCollide()
    {
        await SeedAsync(1);

        var report = await GetAsync(groupBy: "user");

        var group = Assert.Single(report.Groups);
        Assert.NotNull(group.Sublabel);
        Assert.Contains("@", group.Sublabel);
        Assert.Equal(TestUsers.RegularUserId.ToString(), group.Key);
    }

    [Fact]
    public async Task GetUsageReport_GroupedByDay_KeysAndLabelsEachRowAsAnIsoDate()
    {
        await SeedAsync(1, input: 10, output: 0);
        await SeedAsync(2, input: 20, output: 0);

        var report = await GetAsync(groupBy: "day");

        Assert.Equal(2, report.Groups.Count);
        Assert.Equal(_anchor.AddDays(-2).ToString("yyyy-MM-dd"), report.Groups[0].Key);
        Assert.Equal(20, report.Groups[0].TotalTokens);
        Assert.All(report.Groups, group => Assert.Equal(group.Key, group.Label));
    }

    [Fact]
    public async Task GetUsageReport_GroupedByModel_ReusesTheGroupsForTheTopModelsChart()
    {
        await SeedAsync(1);

        var report = await GetAsync(groupBy: "model");

        Assert.Equal(
            report.Groups.Select(group => group.Key),
            report.TopModels.Select(group => group.Key));
    }

    [Fact]
    public async Task GetUsageReport_GroupedByDay_StillReportsTopModels()
    {
        await SeedAsync(1);

        var report = await GetAsync(groupBy: "day");

        var top = Assert.Single(report.TopModels);
        Assert.Equal(KnownIds.SeedModelId.ToString(), top.Key);
    }

    [Fact]
    public async Task GetUsageReport_Series_IsDenseOrderedAndBucketedByUtcDay()
    {
        await SeedAsync(1, input: 5, output: 0);

        var report = await GetAsync(fromDaysAgo: 3);

        Assert.Equal(4, report.Series.Count);
        Assert.Equal(report.Series.OrderBy(point => point.Date), report.Series);
        Assert.Equal(5, report.Series.Sum(point => point.TotalTokens));
        Assert.Equal(3, report.Series.Count(point => point.TotalTokens == 0));

        var busy = Assert.Single(report.Series, point => point.TotalTokens > 0);
        Assert.Equal(DateOnly.FromDateTime(_anchor.AddDays(-1).UtcDateTime), busy.Date);
    }

    [Fact]
    public async Task GetUsageReport_SeriesGroupsAndTotals_AgreeOnWhatTheWindowHolds()
    {
        await SeedAsync(1, input: 10, output: 0);
        await SeedAsync(2, input: 20, output: 0, toolInput: 5);
        await SeedAsync(3, input: 30, output: 0, kind: ConversationUsageKinds.Naming);

        var report = await GetAsync();

        Assert.Equal(report.Totals.TotalTokens, report.Series.Sum(point => point.TotalTokens));
        Assert.Equal(report.Totals.TotalTokens, report.Groups.Sum(group => group.TotalTokens));
    }

    [Fact]
    public async Task GetUsageReport_NoBounds_ReportsTheLastThirtyDays()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var report = await client.GetFromJsonAsync<UsageReportDto>(
            "api/reports/usage", TestContext.Current.CancellationToken);

        Assert.NotNull(report);
        Assert.Equal(30, (report.To - report.From).TotalDays, 3);
        Assert.Equal("model", report.GroupBy);
        Assert.Equal(500, report.GroupLimit);
        Assert.False(report.GroupsTruncated);
    }

    [Fact]
    public async Task GetUsageReport_FromAfterTo_ReturnsValidationProblemNamingTheParameter()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync(
            Route(_anchor, _anchor.AddDays(-1)), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("/problems/validation-error", problem.Type);
        Assert.True(problem.Errors.ContainsKey("from"));
    }

    [Fact]
    public async Task GetUsageReport_RangeBeyondTheConfiguredMaximum_ReturnsValidationProblem()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync(
            Route(_anchor.AddYears(-4), _anchor), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("366", Assert.Single(problem.Errors["from"]));
    }

    [Fact]
    public async Task GetUsageReport_UnrecognisedGrouping_ReturnsValidationProblemNamingGroupBy()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync(
            "api/reports/usage?groupBy=conversation", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("model", Assert.Single(problem.Errors["groupBy"]));
    }

    [Fact]
    public async Task GetUsageReport_SerializesCamelCase_WhichIsWhatTheClientMirrorExpects()
    {
        await SeedAsync(1);

        using var client = _fixture.Factory.CreateAdminClient();

        var body = await client.GetStreamAsync(
            Route(_anchor.AddDays(-5), _anchor), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(
            body, cancellationToken: TestContext.Current.CancellationToken);

        // Nothing in this API configures a naming policy, so minimal APIs' Web defaults apply.
        // Asserting it rather than assuming it, because the TypeScript mirror in
        // `domain/api/usage-report.ts` is written against these exact names, and a deserializer
        // reading case-insensitively — which every test that round-trips a DTO does — would hide
        // the day that stopped being true.
        var names = PropertyNames(document.RootElement).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("groupBy", names);
        Assert.Contains("priorTotals", names);
        Assert.Contains("topModels", names);
        Assert.Contains("groupsTruncated", names);
        Assert.Contains("estimatedCost", names);
        Assert.Contains("unpricedTurns", names);
        Assert.DoesNotContain("GroupBy", names);
    }

    [Fact]
    public async Task GetUsageReport_CarriesNoPromptContentToolArgumentsOrToolResults()
    {
        await SeedAsync(1);

        using var client = _fixture.Factory.CreateAdminClient();

        var body = await client.GetStreamAsync(
            Route(_anchor.AddDays(-5), _anchor), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);

        // Property names, not the whole document. The audit trail stores no message text, no tool
        // arguments and no tool results anywhere, so the guarantee is that no *field* on the wire
        // could carry one — and scanning values instead would fail the day a model is called
        // "Prompt Engineer" while proving nothing extra.
        foreach (var name in PropertyNames(document.RootElement))
        {
            Assert.DoesNotContain("content", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("argument", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("result", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;

                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    [Fact]
    public async Task GetUsageReport_EndInTheFuture_IsClampedAndTheClampedWindowIsEchoed()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var report = await client.GetFromJsonAsync<UsageReportDto>(
            Route(_anchor.AddDays(-5), _anchor.AddYears(1)), TestContext.Current.CancellationToken);

        Assert.NotNull(report);
        // Honoured, the window would be a year of dense zero days drawn as a collapse in spending
        // rather than as time that has not happened. `To` is on the contract so the clamp is
        // visible rather than silent.
        Assert.True(report.To <= DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(report.Series.Count < 400);
    }

    [Fact]
    public async Task GetUsageReport_GroupsTiedOnTokens_ComeBackInAStableOrder()
    {
        var first = await _fixture.AddModelAsync("tie-a", cancellationToken: TestContext.Current.CancellationToken);
        var second = await _fixture.AddModelAsync("tie-b", cancellationToken: TestContext.Current.CancellationToken);
        await SeedForModelAsync(first, input: 100);
        await SeedForModelAsync(second, input: 100);
        await SeedForModelAsync(second, input: 0);

        var one = await GetAsync(groupBy: "model");
        var two = await GetAsync(groupBy: "model");

        // Without a tiebreaker the order among equal-token groups is whatever the engine returns,
        // which decides *which* groups survive `MaxGroups` — so the same request could answer with
        // a different set of rows twice in a row. The turn count breaks it.
        Assert.Equal(one.Groups.Select(group => group.Key), two.Groups.Select(group => group.Key));
        Assert.Equal("tie-b", one.Groups[0].Label);
    }

    [Fact]
    public async Task GetUsageReport_GroupPricedAtZero_ReportsZeroRatherThanNull()
    {
        // A genuinely free model is priced, and its cost is zero. The distinction this asserts is
        // the one the whole nullable-cost design exists for: zero means free, null means nobody
        // knows, and a group must not collapse the first into the second.
        var free = await _fixture.AddModelAsync("free-model", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationUsageAsync(
            _conversationId, free, _anchor.AddDays(-1),
            inputTokens: 1000, outputTokens: 1000,
            inputPricePerMillionTokens: 0m, outputPricePerMillionTokens: 0m,
            cancellationToken: TestContext.Current.CancellationToken);

        var report = await GetAsync(groupBy: "model");

        var group = Assert.Single(report.Groups);
        Assert.Equal(0m, group.EstimatedCost);
        Assert.Equal(0, group.UnpricedTurns);
        Assert.Equal(0m, report.Totals.EstimatedCost);
    }

    [Fact]
    public async Task GetUsageReport_MixedPricingAcrossGroups_KeepsEachGroupsCostSeparate()
    {
        var priced = await _fixture.AddModelAsync("priced-model", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationUsageAsync(
            _conversationId, priced, _anchor.AddDays(-1),
            inputTokens: 1_000_000, outputTokens: 0,
            inputPricePerMillionTokens: 3m, outputPricePerMillionTokens: 3m,
            cancellationToken: TestContext.Current.CancellationToken);
        await SeedAsync(1, input: 1_000_000, output: 0);

        var report = await GetAsync(groupBy: "model");

        var pricedGroup = Assert.Single(report.Groups, group => group.Label == "priced-model");
        var unpricedGroup = Assert.Single(report.Groups, group => group.Label != "priced-model");

        Assert.Equal(3m, pricedGroup.EstimatedCost);
        Assert.Equal(0, pricedGroup.UnpricedTurns);
        Assert.Null(unpricedGroup.EstimatedCost);
        Assert.Equal(1, unpricedGroup.UnpricedTurns);
        // The period total covers the priced calls only, and says how many it could not price.
        Assert.Equal(3m, report.Totals.EstimatedCost);
        Assert.Equal(1, report.Totals.UnpricedTurns);
    }

    [Fact]
    public async Task GetUsageReport_ModelRenamedAfterTheCall_RelabelsHistoryButDoesNotReprice()
    {
        var modelId = await _fixture.AddModelAsync("original-name", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationUsageAsync(
            _conversationId, modelId, _anchor.AddDays(-1),
            inputTokens: 1_000_000, outputTokens: 0,
            inputPricePerMillionTokens: 2m, outputPricePerMillionTokens: 2m,
            cancellationToken: TestContext.Current.CancellationToken);

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
            var model = await ctx.Models
                .Where(x => x.Id == modelId)
                .SingleAsync(TestContext.Current.CancellationToken);
            model.Name = "renamed";
            model.InputPricePerMillionTokens = 99m;
            model.OutputPricePerMillionTokens = 99m;
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var report = await GetAsync(groupBy: "model");

        var group = Assert.Single(report.Groups);
        // The label follows the catalog, so history is readable under the name the model has now.
        Assert.Equal("renamed", group.Label);
        // The figures follow the snapshot taken when the call ran, so repricing the catalog cannot
        // rewrite what a past call was charged.
        Assert.Equal(2m, group.EstimatedCost);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("user")]
    [InlineData("day")]
    public async Task GetUsageReport_EveryGrouping_SumsBackToThePeriodTotals(string groupBy)
    {
        var other = await _fixture.AddUserAsync(
            "Sum", "Check", "sum-check@example.com", cancellationToken: TestContext.Current.CancellationToken);
        var second = await _fixture.AddModelAsync("second-model", cancellationToken: TestContext.Current.CancellationToken);
        await SeedAsync(1, input: 10, output: 0);
        await SeedAsync(2, input: 20, output: 0, toolInput: 5, userId: other);
        await SeedAsync(3, input: 30, output: 0, kind: ConversationUsageKinds.Naming);
        await SeedForModelAsync(second, input: 40);

        var report = await GetAsync(groupBy: groupBy);

        Assert.Equal(report.Totals.TotalTokens, report.Groups.Sum(group => group.TotalTokens));
        Assert.Equal(report.Totals.Turns, report.Groups.Sum(group => group.Turns));
    }

    [Fact]
    public async Task GetUsageReport_ManyModels_OrdersGroupsAndBarsHeaviestFirst()
    {
        var heavy = await _fixture.AddModelAsync("heavy-model", cancellationToken: TestContext.Current.CancellationToken);
        var light = await _fixture.AddModelAsync("light-model", cancellationToken: TestContext.Current.CancellationToken);
        await SeedForModelAsync(heavy, input: 900);
        await SeedForModelAsync(light, input: 50);
        await SeedAsync(1, input: 300, output: 0);

        var report = await GetAsync(groupBy: "model");

        Assert.Equal([900, 300, 50], report.Groups.Select(group => group.TotalTokens));
        Assert.Equal(report.Groups.Select(group => group.Key), report.TopModels.Select(group => group.Key));
        Assert.Equal("heavy-model", report.Groups[0].Label);
    }

    [Fact]
    public async Task GetUsageReport_MoreModelsThanTheChartDraws_CapsTheBarsAtTen()
    {
        for (var index = 0; index < 12; index++)
        {
            var modelId = await _fixture.AddModelAsync(
                $"model-{index:00}", cancellationToken: TestContext.Current.CancellationToken);
            await SeedForModelAsync(modelId, input: (index + 1) * 10);
        }

        var report = await GetAsync(groupBy: "model");

        Assert.Equal(12, report.Groups.Count);
        Assert.Equal(10, report.TopModels.Count);
        Assert.Equal(report.Groups.Take(10).Select(group => group.Key), report.TopModels.Select(group => group.Key));
    }

    [Fact]
    public async Task GetUsageReport_MoreGroupsThanTheLimit_TruncatesHeaviestFirstAndSaysSo()
    {
        var light = await _fixture.AddUserAsync(
            "Light", "User", "light-report@example.com", cancellationToken: TestContext.Current.CancellationToken);
        var heavy = await _fixture.AddUserAsync(
            "Heavy", "User", "heavy-report@example.com", cancellationToken: TestContext.Current.CancellationToken);
        await SeedAsync(1, input: 1, output: 0);
        await SeedAsync(1, input: 50, output: 0, userId: light);
        await SeedAsync(1, input: 900, output: 0, userId: heavy);

        var report = await ReportWithOptionsAsync(new ReportOptions { MaxGroups = 2 }, "user");

        Assert.True(report.GroupsTruncated);
        Assert.Equal(2, report.GroupLimit);
        Assert.Equal([900, 50], report.Groups.Select(group => group.TotalTokens));
        // The headline figures are computed over the whole window, so truncating the breakdown
        // never makes them wrong — only incomplete as a breakdown.
        Assert.Equal(951, report.Totals.TotalTokens);
    }

    [Fact]
    public async Task GetUsageReport_LimitBelowTheChartSize_StillDrawsTenBars()
    {
        for (var index = 0; index < 12; index++)
        {
            var modelId = await _fixture.AddModelAsync(
                $"capped-{index:00}", cancellationToken: TestContext.Current.CancellationToken);
            await SeedForModelAsync(modelId, input: (index + 1) * 10);
        }

        var report = await ReportWithOptionsAsync(new ReportOptions { MaxGroups = 3 }, "model");

        Assert.True(report.GroupsTruncated);
        Assert.Equal(3, report.Groups.Count);
        Assert.Equal(10, report.TopModels.Count);
    }

    /// <summary>
    /// Runs the service directly against the real database, for the behaviours that need a
    /// configuration the shared test host does not carry.
    /// </summary>
    /// <remarks>
    /// One factory serves the whole integration collection, so <c>Reports:MaxGroups</c> cannot be
    /// varied over HTTP without standing up a second host. The engine, the schema and the query
    /// are the same either way; only the transport is skipped.
    /// </remarks>
    private async Task<UsageReportDto> ReportWithOptionsAsync(ReportOptions options, string? groupBy)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        var service = new ReportService(ctx, Options.Create(options), NullLogger<ReportService>.Instance);

        return await service.GetUsageReportAsync(
            _anchor.AddDays(-5), _anchor, groupBy, TestContext.Current.CancellationToken);
    }

    private string Route(DateTimeOffset from, DateTimeOffset to) =>
        $"api/reports/usage?from={Uri.EscapeDataString(from.ToString("O"))}"
        + $"&to={Uri.EscapeDataString(to.ToString("O"))}";
}
