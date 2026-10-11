using System.Globalization;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Reports;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Reports.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Reports;

/// <summary>Reads aggregated usage out of the append-only <c>Core.ConversationUsage</c> audit trail.</summary>
public interface IReportService
{
    /// <summary>Aggregates the model calls in a window, grouped one way, beside the prior window.</summary>
    /// <param name="from">
    /// Inclusive start, or <see langword="null"/> for thirty days before <paramref name="to"/>.
    /// </param>
    /// <param name="to">Exclusive end, or <see langword="null"/> for now.</param>
    /// <param name="groupBy">
    /// A <see cref="UsageGroupKeyNames"/> token, or <see langword="null"/> for the default.
    /// </param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <exception cref="ValidationException">
    /// <paramref name="from"/> is not before <paramref name="to"/>, the window exceeds
    /// <c>Reports:MaxRangeDays</c>, or <paramref name="groupBy"/> names no accepted dimension.
    /// </exception>
    Task<UsageReportDto> GetUsageReportAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? groupBy,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Queries port <c>docs/conversations/usage-and-reporting.md</c>: assistant and tool tokens are summed apart,
/// <c>ContextTokens</c> is never added to billed columns, and the turn and tool-call child tables are never read
/// (joining either would double-count). The window is half-open, so consecutive periods tile exactly.
/// </remarks>
public partial class ReportService(
    EnterpriseGptDbContext ctx,
    IOptions<ReportOptions> options,
    ILogger<ReportService> logger,
    TimeProvider timeProvider) : IReportService
{
    /// <summary>
    /// The <c>errors</c> key for a rejected window; both bounds use it because a range control moves the start.
    /// </summary>
    private const string FromProperty = "from";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>How many rows the "top models" chart draws.</summary>
    private const int TopModelCount = 10;

    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly ReportOptions _options = options.Value;
    private readonly ILogger<ReportService> _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<UsageReportDto> GetUsageReportAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? groupBy,
        CancellationToken cancellationToken = default)
    {
        var groupKey = UsageGroupKeyNames.Resolve(groupBy);
        var (start, end) = ResolveWindow(from, to);
        var span = end - start;

        var current = _ctx.ConversationUsage.AsNoTracking()
            .Where(x => x.DateCreated >= start && x.DateCreated < end);
        var prior = _ctx.ConversationUsage.AsNoTracking()
            .Where(x => x.DateCreated >= start - span && x.DateCreated < start);

        var totals = await ReadTotalsAsync(current, cancellationToken);
        var priorTotals = await ReadTotalsAsync(prior, cancellationToken);

        // Never truncated: a bounded window holds at most `MaxRangeDays` days. These are UTC days only
        // because every row is written UTC; DATEPART buckets by the stored offset, normalising in SQL needs
        // an EF Core 11 translation, and `BuildSeries` logs any bucket that strays outside the window.
        var days = await Aggregate(current.GroupBy(x => new { x.DateCreated.Year, x.DateCreated.Month, x.DateCreated.Day }))
            .ToListAsync(cancellationToken);
        var byDay = days.ToDictionary(
            row => new DateOnly(row.Key.Year, row.Key.Month, row.Key.Day),
            row => row);

        var limit = _options.MaxGroups;

        // One past the limit to detect truncation, and never fewer than the top-models chart needs,
        // since grouping by model serves that chart from this same list.
        var depth = Math.Max(limit, TopModelCount) + 1;
        var fetched = groupKey == UsageGroupKeys.Day
            // The day breakdown is the series re-sorted, so it is not queried again.
            ? Rank(byDay.Select(entry => ToGroup(
                entry.Value,
                entry.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                entry.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                null)), depth)
            : await ReadGroupsAsync(current, groupKey, depth, cancellationToken);

        var truncated = fetched.Count > limit;
        var groups = truncated ? [.. fetched.Take(limit)] : fetched;

        return new UsageReportDto
        {
            From = start,
            To = end,
            GroupBy = UsageGroupKeyNames.ToToken(groupKey),
            Totals = totals.Report,
            PriorTotals = priorTotals.Report,
            Outcomes = totals.Outcomes,
            Series = BuildSeries(start, end, byDay),
            Groups = groups,
            // From what was fetched, not what survived truncation, so the chart draws ten where ten exist.
            TopModels = groupKey == UsageGroupKeys.Model
                ? [.. fetched.Take(TopModelCount)]
                : await ReadGroupsAsync(current, UsageGroupKeys.Model, TopModelCount, cancellationToken),
            GroupsTruncated = truncated,
            GroupLimit = limit
        };
    }

    private readonly record struct PeriodTotals(
        UsageReportTotalsDto Report, IReadOnlyList<UsageOutcomeDto> Outcomes);

    #region Private methods

    /// <remarks>
    /// An over-long window is rejected, unlike paging arguments, because a silently shortened one reads as the
    /// full period's spend.
    /// </remarks>
    private (DateTimeOffset Start, DateTimeOffset End) ResolveWindow(DateTimeOffset? from, DateTimeOffset? to)
    {
        var now = _timeProvider.GetUtcNow();

        // A future end is clamped, since trailing zero days would draw as a collapse in spending.
        // The response echoes the clamped bound.
        var end = to is { } requested && requested < now ? requested : now;
        var start = from ?? end - DefaultWindow;

        if (start >= end)
        {
            throw new ValidationException(
            [
                new ValidationFailure(FromProperty, "'from' must be earlier than 'to'.")
            ]);
        }

        if ((end - start).TotalDays > _options.MaxRangeDays)
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    FromProperty,
                    $"The reported range cannot exceed {_options.MaxRangeDays} days.")
            ]);
        }

        return (start, end);
    }

    /// <remarks>
    /// One query grouped by outcome and kind (at most six buckets) yields both totals and the outcome split.
    /// The distinct counts are separate queries because counting per bucket would double-count users.
    /// </remarks>
    private static async Task<PeriodTotals> ReadTotalsAsync(
        IQueryable<ConversationUsage> scoped, CancellationToken cancellationToken)
    {
        var buckets = await Aggregate(scoped.GroupBy(x => new { x.Status, x.Kind }))
            .ToListAsync(cancellationToken);

        var activeUsers = await scoped.Select(x => x.UserId).Distinct().CountAsync(cancellationToken);
        var conversations = await scoped.Select(x => x.ConversationId).Distinct().CountAsync(cancellationToken);

        var totals = new UsageReportTotalsDto
        {
            Turns = buckets.Sum(row => row.Turns),
            CompletedTurns = TurnsWith(ConversationUsageStatuses.Completed),
            CancelledTurns = TurnsWith(ConversationUsageStatuses.Cancelled),
            FailedTurns = TurnsWith(ConversationUsageStatuses.Failed),
            NamingTurns = buckets.Sum(row => row.NamingTurns),
            NamingTokens = buckets.Sum(row => row.NamingTokens),
            AssistantTokens = buckets.Sum(row => row.AssistantTokens),
            ToolTokens = buckets.Sum(row => row.ToolTokens),
            TotalTokens = buckets.Sum(row => row.TotalTokens),
            ContextTokens = buckets.Sum(row => row.ContextTokens),
            EstimatedCost = FoldCost(buckets),
            UnpricedTurns = buckets.Sum(row => row.UnpricedTurns),
            ActiveUsers = activeUsers,
            Conversations = conversations
        };

        // Only outcomes that occurred; a zero row would draw an empty legend entry and arc.
        var outcomes = buckets
            .GroupBy(row => row.Key.Status)
            .Select(group => new UsageOutcomeDto
            {
                Status = group.Key.ToString(),
                Turns = group.Sum(row => row.Turns),
                TotalTokens = group.Sum(row => row.TotalTokens)
            })
            .OrderBy(outcome => outcome.Status)
            .ToList();

        return new PeriodTotals(totals, outcomes);

        // Not static: it captures `buckets`, whose key type is anonymous.
        long TurnsWith(ConversationUsageStatuses status) =>
            buckets.Where(row => row.Key.Status == status).Sum(row => row.Turns);
    }

    /// <remarks>
    /// Each arm groups by identifier and label so the label needs no second lookup. Labels are the catalog's
    /// current ones while figures are per-call snapshots; see <see cref="UsageGroupDto.Label"/>.
    /// </remarks>
    private static async Task<List<UsageGroupDto>> ReadGroupsAsync(
        IQueryable<ConversationUsage> scoped,
        UsageGroupKeys key,
        int take,
        CancellationToken cancellationToken)
    {
        switch (key)
        {
            case UsageGroupKeys.Model:
                {
                    var rows = await Heaviest(Aggregate(scoped.GroupBy(x => new { x.ModelId, x.Model.Name })), take)
                        .ToListAsync(cancellationToken);

                    return [.. rows.Select(row => ToGroup(row, row.Key.ModelId.ToString(), row.Key.Name, null))];
                }

            case UsageGroupKeys.Provider:
                {
                    var rows = await Heaviest(Aggregate(scoped.GroupBy(x => new { x.ProviderId, x.Provider.Name })), take)
                        .ToListAsync(cancellationToken);

                    return [.. rows.Select(row => ToGroup(row, row.Key.ProviderId.ToString(), row.Key.Name, null))];
                }

            case UsageGroupKeys.User:
                {
                    // Name assembled in memory: `User.FullName` is unmapped, and concatenating in SQL
                    // would add a computed expression to the GROUP BY.
                    var rows = await Heaviest(
                            Aggregate(scoped.GroupBy(x => new { x.UserId, x.User.FirstName, x.User.LastName, x.User.Email })),
                            take)
                        .ToListAsync(cancellationToken);

                    return
                    [
                        .. rows.Select(row => ToGroup(
                        row,
                        row.Key.UserId.ToString(),
                        $"{row.Key.FirstName} {row.Key.LastName}".Trim(),
                        row.Key.Email))
                    ];
                }

            // Days never reach here: the series already holds that aggregate.
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Unsupported usage grouping.");
        }
    }

    /// <remarks>One point per day in the window, because a sparse line would compress quiet stretches.</remarks>
    private List<UsageSeriesPointDto> BuildSeries<TKey>(
        DateTimeOffset start, DateTimeOffset end, Dictionary<DateOnly, AggregateRow<TKey>> byDay)
        where TKey : notnull
    {
        var first = DateOnly.FromDateTime(start.UtcDateTime);
        // Half-open window: an end exactly on midnight belongs to the day before.
        var last = DateOnly.FromDateTime(end.AddTicks(-1).UtcDateTime);

        // A bucket outside the window is about to be dropped, silently disagreeing with the totals.
        // Only a row stored with a non-UTC offset produces one, so log it.
        var stray = byDay.Keys.Where(day => day < first || day > last).ToArray();
        if (stray.Length > 0 && _logger.IsEnabled(LogLevel.Warning))
        {
            var days = string.Join(", ", stray);
            LogStrayDayBuckets(_logger, stray.Length, start, end, days);
        }

        List<UsageSeriesPointDto> series = [];
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            series.Add(byDay.TryGetValue(day, out var row)
                ? new UsageSeriesPointDto
                {
                    Date = day,
                    TotalTokens = row.TotalTokens,
                    EstimatedCost = row.EstimatedCost
                }
                : new UsageSeriesPointDto { Date = day, TotalTokens = 0, EstimatedCost = null });
        }

        return series;
    }

    /// <remarks>Turns break ties so the groups that survive the <c>MaxGroups</c> cut are deterministic.</remarks>
    private static IQueryable<AggregateRow<TKey>> Heaviest<TKey>(IQueryable<AggregateRow<TKey>> rows, int take)
        where TKey : notnull =>
        rows.OrderByDescending(row => row.TotalTokens).ThenByDescending(row => row.Turns).Take(take);

    /// <summary>In-memory counterpart of <c>Heaviest</c> for the day breakdown; ties break on the date.</summary>
    private static List<UsageGroupDto> Rank(IEnumerable<UsageGroupDto> groups, int take) =>
    [
        .. groups
            .OrderByDescending(group => group.TotalTokens)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(take)
    ];

    private static UsageGroupDto ToGroup<TKey>(AggregateRow<TKey> row, string key, string label, string? sublabel)
        where TKey : notnull =>
        new()
        {
            Key = key,
            Label = label,
            Sublabel = sublabel,
            Turns = row.Turns,
            CompletedTurns = row.CompletedTurns,
            CancelledTurns = row.CancelledTurns,
            FailedTurns = row.FailedTurns,
            AssistantTokens = row.AssistantTokens,
            ToolTokens = row.ToolTokens,
            TotalTokens = row.TotalTokens,
            EstimatedCost = row.EstimatedCost,
            UnpricedTurns = row.UnpricedTurns
        };

    /// <summary>The one projection every grouped query here shares, so the arithmetic exists once.</summary>
    /// <remarks>
    /// Cost is recomputed because <c>ConversationUsage.EstimatedCost</c> is an unmapped getter and cannot be
    /// projected.
    /// </remarks>
    private static IQueryable<AggregateRow<TKey>> Aggregate<TKey>(
        IQueryable<IGrouping<TKey, ConversationUsage>> grouped)
        where TKey : notnull =>
        grouped.Select(group => new AggregateRow<TKey>
        {
            Key = group.Key,
            Turns = group.LongCount(),
            CompletedTurns = group.LongCount(x => x.Status == ConversationUsageStatuses.Completed),
            CancelledTurns = group.LongCount(x => x.Status == ConversationUsageStatuses.Cancelled),
            FailedTurns = group.LongCount(x => x.Status == ConversationUsageStatuses.Failed),
            NamingTurns = group.LongCount(x => x.Kind == ConversationUsageKinds.Naming),
            NamingTokens = group.Sum(x => x.Kind == ConversationUsageKinds.Naming
                ? x.InputTokens + x.OutputTokens + x.ToolInputTokens + x.ToolOutputTokens
                : 0L),
            AssistantTokens = group.Sum(x => x.InputTokens + x.OutputTokens),
            ToolTokens = group.Sum(x => x.ToolInputTokens + x.ToolOutputTokens),
            TotalTokens = group.Sum(x =>
                x.InputTokens + x.OutputTokens + x.ToolInputTokens + x.ToolOutputTokens),
            // Null (counted as zero) for every untranscribed turn, i.e. each cancelled or failed one.
            // Reported beside the billed columns, never added to them.
            ContextTokens = group.Sum(x => x.ContextTokens ?? 0L),
            PricedCost = group.Sum(x =>
                x.InputPricePerMillionTokens != null && x.OutputPricePerMillionTokens != null
                    ? (((x.InputTokens + x.ToolInputTokens) * x.InputPricePerMillionTokens)
                        + ((x.OutputTokens + x.ToolOutputTokens) * x.OutputPricePerMillionTokens))
                        / 1_000_000m
                    : (decimal?)null),
            UnpricedTurns = group.LongCount(x =>
                x.InputPricePerMillionTokens == null || x.OutputPricePerMillionTokens == null)
        });

    #endregion

    #region Public static methods

    /// <summary>
    /// Sums the priceable groups' cost, or <see langword="null"/> (not a zero that reads as free) when none are.
    /// </summary>
    internal static decimal? FoldCost<TKey>(IEnumerable<AggregateRow<TKey>> rows)
        where TKey : notnull
    {
        decimal? total = null;
        foreach (var row in rows)
        {
            if (row.EstimatedCost is { } cost)
            {
                total = (total ?? 0m) + cost;
            }
        }

        return total;
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Usage report dropped {StrayDayCount} day bucket(s) outside the reported window {From:o}..{To:o}: {StrayDays}. Rows are expected to carry a UTC offset.")]
    private static partial void LogStrayDayBuckets(
        ILogger logger, int strayDayCount, DateTimeOffset from, DateTimeOffset to, string strayDays);

    #endregion
}
