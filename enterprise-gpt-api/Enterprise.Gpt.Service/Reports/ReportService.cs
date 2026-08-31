using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Settings;

namespace Enterprise.Gpt.Service.Reports;

/// <summary>
/// Reads aggregated usage out of the append-only <c>Core.ConversationUsage</c> audit trail.
/// </summary>
public interface IReportService
{
    /// <summary>
    /// Aggregates every model call in a window, grouped one way, with the prior window beside it.
    /// </summary>
    /// <param name="from">
    /// The inclusive start of the window, or <see langword="null"/> for thirty days before
    /// <paramref name="to"/>.
    /// </param>
    /// <param name="to">The exclusive end of the window, or <see langword="null"/> for now.</param>
    /// <param name="groupBy">
    /// The dimension to break the window down by, or <see langword="null"/> for the default. See
    /// <see cref="UsageGroupKeys"/>.
    /// </param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ValidationException">
    /// <paramref name="from"/> is not before <paramref name="to"/>, the window is longer than
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
/// <para>
/// Every query here is a port of one in <c>docs/conversations/usage-and-reporting.md</c>, and
/// the three rules stated above those queries are what shape this file: the assistant's own token
/// columns and its tools' are summed apart and combined only in a total; the estimated
/// <c>ContextTokens</c> sits beside the billed columns and is never added to them; and neither
/// <c>Core.ConversationUsageTurn</c> nor <c>Core.ConversationUsageToolCall</c> is read at all,
/// because turn rows partition the parent's columns and tool-call rows are already rolled up into
/// it. Joining either would double-count.
/// </para>
/// <para>
/// The window is half-open — <c>[from, to)</c> — so consecutive periods tile without a row falling
/// into both, which is what makes the prior-period comparison arithmetic rather than an estimate.
/// </para>
/// </remarks>
public class ReportService(
    EnterpriseGptDbContext ctx,
    IOptions<ReportOptions> options,
    ILogger<ReportService> logger) : IReportService
{
    /// <summary>
    /// The <c>errors</c> key a rejected window is reported under. Both bounds are reported against
    /// <c>from</c>, because a client with a range control adjusts the start rather than the end.
    /// </summary>
    private const string FromProperty = "from";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>How many rows the "top models" chart draws. Frame <c>5j</c> draws ten.</summary>
    private const int TopModelCount = 10;

    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly ReportOptions _options = options.Value;
    private readonly ILogger<ReportService> _logger = logger;

    /// <inheritdoc />
    public async Task<UsageReportDto> GetUsageReportAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? groupBy,
        CancellationToken cancellationToken = default)
    {
        var groupKey = UsageGroupKeys.Resolve(groupBy);
        var (start, end) = ResolveWindow(from, to);
        var span = end - start;

        var current = _ctx.ConversationUsage.AsNoTracking()
            .Where(x => x.DateCreated >= start && x.DateCreated < end);
        var prior = _ctx.ConversationUsage.AsNoTracking()
            .Where(x => x.DateCreated >= start - span && x.DateCreated < start);

        var totals = await ReadTotalsAsync(current, cancellationToken);
        var priorTotals = await ReadTotalsAsync(prior, cancellationToken);

        // Never truncated: the series is the horizontal axis of a chart, and a bounded window holds
        // at most `MaxRangeDays` days.
        //
        // **These are UTC days only because every row is written UTC.** The three members below
        // translate to DATEPART over a `datetimeoffset`, which reads the offset stored on the
        // value rather than normalising it — so a row written with a non-UTC offset buckets to its
        // own local day. Every writer is `DateTimeOffset.UtcNow` today (`ConversationService`), and
        // `BuildSeries` reports anything that lands outside the window rather than dropping it, so
        // a future writer that breaks the assumption is loud instead of silently short.
        // Normalising in SQL instead needs `DateTimeOffset.UtcDateTime` or `EF.Functions.DateTrunc`,
        // both of which are EF Core 11 translations and unavailable here.
        var days = await Aggregate(current.GroupBy(x => new { x.DateCreated.Year, x.DateCreated.Month, x.DateCreated.Day }))
            .ToListAsync(cancellationToken);
        var byDay = days.ToDictionary(
            row => new DateOnly(row.Key.Year, row.Key.Month, row.Key.Day),
            row => row);

        var limit = _options.MaxGroups;

        // One row past the limit, so truncation is detectable — and never fewer than the bar chart
        // needs, because when the grouping is already by model that chart is served from this same
        // list. An operator who sets MaxGroups below ten would otherwise get a five-bar "top 10".
        var depth = Math.Max(limit, TopModelCount) + 1;
        var fetched = groupKey == UsageGroupKey.Day
            // The day breakdown is the series re-sorted. Asking the database for it twice would
            // return the same rows in a different order for no benefit.
            ? Rank(byDay.Select(entry => ToGroup(
                entry.Value, entry.Key.ToString("yyyy-MM-dd"), entry.Key.ToString("yyyy-MM-dd"), null)), depth)
            : await ReadGroupsAsync(current, groupKey, depth, cancellationToken);

        var truncated = fetched.Count > limit;
        List<UsageGroupDto> groups = truncated ? [.. fetched.Take(limit)] : fetched;

        return new UsageReportDto
        {
            From = start,
            To = end,
            GroupBy = UsageGroupKeys.ToToken(groupKey),
            Totals = totals.Report,
            PriorTotals = priorTotals.Report,
            Outcomes = totals.Outcomes,
            Series = BuildSeries(start, end, byDay),
            Groups = groups,
            // Taken from what was fetched rather than from what survived truncation, so the chart
            // always draws ten where ten exist.
            TopModels = groupKey == UsageGroupKey.Model
                ? [.. fetched.Take(TopModelCount)]
                : await ReadGroupsAsync(current, UsageGroupKey.Model, TopModelCount, cancellationToken),
            GroupsTruncated = truncated,
            GroupLimit = limit
        };
    }

    /// <summary>
    /// Fills in whichever bounds the caller left out and refuses a window that cannot be reported.
    /// </summary>
    /// <remarks>
    /// Rejected rather than clamped, unlike the paging arguments elsewhere in this API. A caller who
    /// asked for two years and silently received one would read the answer as two years of spend,
    /// and nothing in the response would contradict them — whereas an over-long page of users is
    /// obviously a page.
    /// </remarks>
    private (DateTimeOffset Start, DateTimeOffset End) ResolveWindow(DateTimeOffset? from, DateTimeOffset? to)
    {
        var now = DateTimeOffset.UtcNow;

        // Clamped rather than honoured. A window reaching into the future is inside the maximum and
        // produces a long tail of dense zero days, which draws as a collapse in spending rather
        // than as time that has not happened. The response echoes the clamped bound, which is what
        // `To` is on the contract for.
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

    /// <summary>
    /// Reads one period's headline figures and its outcome split, from a single grouped query.
    /// </summary>
    /// <remarks>
    /// Grouping by outcome and kind rather than aggregating the period whole answers both at once:
    /// there are at most six buckets, and folding them in memory is cheaper than a second scan of
    /// a table nothing prunes. The two distinct counts cannot come from the same query — a count
    /// of distinct users <em>per bucket</em> would double-count anyone whose turns did not all end
    /// the same way.
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

        // Only the outcomes that occurred. A zero row would draw a legend entry and a zero-length
        // arc for something that did not happen.
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

        // A bucket carries exactly one outcome, so this selects buckets rather than summing a
        // conditional column. Not static: it reads `buckets`, whose element type is anonymous.
        long TurnsWith(ConversationUsageStatuses status) =>
            buckets.Where(row => row.Key.Status == status).Sum(row => row.Turns);
    }

    /// <summary>
    /// Reads one period broken down by a dimension, heaviest first.
    /// </summary>
    /// <remarks>
    /// Each arm groups by the identifier <em>and</em> the label, so the label comes back without a
    /// second lookup. That widens the aggregate key by the label columns — one string for a model
    /// or a provider, three for a user — which is worth it at this cardinality and is the reason
    /// the user arm is the widest of the three. The label is the catalog's current one while every
    /// figure is the snapshot taken when each call ran; see <see cref="UsageGroupDto.Label"/> for
    /// why those two are meant to disagree.
    /// </remarks>
    private static async Task<List<UsageGroupDto>> ReadGroupsAsync(
        IQueryable<ConversationUsage> scoped,
        UsageGroupKey key,
        int take,
        CancellationToken cancellationToken)
    {
        switch (key)
        {
            case UsageGroupKey.Model:
            {
                var rows = await Heaviest(Aggregate(scoped.GroupBy(x => new { x.ModelId, x.Model.Name })), take)
                    .ToListAsync(cancellationToken);

                return [.. rows.Select(row => ToGroup(row, row.Key.ModelId.ToString(), row.Key.Name, null))];
            }

            case UsageGroupKey.Provider:
            {
                var rows = await Heaviest(Aggregate(scoped.GroupBy(x => new { x.ProviderId, x.Provider.Name })), take)
                    .ToListAsync(cancellationToken);

                return [.. rows.Select(row => ToGroup(row, row.Key.ProviderId.ToString(), row.Key.Name, null))];
            }

            case UsageGroupKey.User:
            {
                // The name is assembled here rather than in the query. `User.FullName` is an
                // unmapped getter, and concatenating in SQL would put a computed expression in the
                // GROUP BY for a value the four key columns already determine.
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

            // Days never reach here: the series already holds that aggregate, and asking for it a
            // second time would return the same rows in a different order.
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Unsupported usage grouping.");
        }
    }

    /// <summary>
    /// Expands the day buckets that have traffic into one entry per day in the window.
    /// </summary>
    /// <remarks>
    /// Dense on purpose: a line drawn from a sparse series compresses quiet stretches, so the
    /// shape of the chart stops being a function of time.
    /// </remarks>
    private List<UsageSeriesPointDto> BuildSeries<TKey>(
        DateTimeOffset start, DateTimeOffset end, IReadOnlyDictionary<DateOnly, AggregateRow<TKey>> byDay)
        where TKey : notnull
    {
        var first = DateOnly.FromDateTime(start.UtcDateTime);
        // The window is half-open, so an end that lands exactly on midnight belongs to the day
        // before it — the final tick inside the window is the one that names the last day.
        var last = DateOnly.FromDateTime(end.AddTicks(-1).UtcDateTime);

        // A bucket outside the window is a row this method is about to drop, and dropping it would
        // make the series disagree with the totals in a spend report — with nothing on screen to
        // say so. The only way to produce one is a row stored with a non-UTC offset, so this is
        // where that assumption fails loudly instead of quietly.
        var stray = byDay.Keys.Where(day => day < first || day > last).ToArray();
        if (stray.Length > 0)
        {
            _logger.LogWarning(
                "Usage report dropped {StrayDayCount} day bucket(s) outside the reported window " +
                "{From:o}..{To:o}: {StrayDays}. Rows are expected to carry a UTC offset.",
                stray.Length, start, end, string.Join(", ", stray));
        }

        var series = new List<UsageSeriesPointDto>();
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

    /// <summary>Orders a grouped set the way every consumer of it reads: heaviest first.</summary>
    /// <remarks>
    /// The turn count breaks a tie, because without one the order among equal-token groups is
    /// whatever the engine returns. That is not cosmetic at the truncation boundary: it decides
    /// *which* groups survive `MaxGroups`, so the same request could answer with a different set
    /// of rows twice in a row.
    /// </remarks>
    private static IQueryable<AggregateRow<TKey>> Heaviest<TKey>(IQueryable<AggregateRow<TKey>> rows, int take)
        where TKey : notnull =>
        rows.OrderByDescending(row => row.TotalTokens).ThenByDescending(row => row.Turns).Take(take);

    /// <summary>The in-memory counterpart, for the day breakdown the series already holds.</summary>
    /// <remarks>
    /// Ties break on the key rather than the turn count, which for days is the date — the one
    /// ordering a reader would expect two equally busy days to fall into.
    /// </remarks>
    private static List<UsageGroupDto> Rank(IEnumerable<UsageGroupDto> groups, int take) =>
    [
        .. groups
            .OrderByDescending(group => group.TotalTokens)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(take)
    ];

    /// <summary>
    /// Adds the cost of every group that could be priced, or <see langword="null"/> when none could.
    /// </summary>
    /// <remarks>
    /// Null and zero mean different things here and the distinction survives the fold: a group
    /// whose calls all lacked a price contributes nothing, and a period in which <em>no</em> group
    /// could be priced answers null rather than a total that reads as free.
    /// </remarks>
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

    private readonly record struct PeriodTotals(
        UsageReportTotalsDto Report, IReadOnlyList<UsageOutcomeDto> Outcomes);

    /// <summary>
    /// The one projection every grouped query in this file shares.
    /// </summary>
    /// <remarks>
    /// Generic over the grouping key so the aggregate arithmetic is written once. Getting it wrong
    /// in one of five copies — summing tool tokens into the assistant column, or pricing an
    /// unpriced call as zero — is the kind of defect that produces a plausible number nobody can
    /// tell is wrong, which is exactly what this file exists to avoid.
    /// <para>
    /// The cost arithmetic is repeated here rather than read from
    /// <c>ConversationUsage.EstimatedCost</c>, which is an unmapped getter: projecting it would
    /// throw, because there is no column to translate it to.
    /// </para>
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
            // Zero for a turn that was never transcribed, which is every cancelled and failed one.
            // Folding them in keeps this comparable with the billed columns beside it; it is never
            // added to them.
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
}

/// <summary>
/// One grouped row as the database returns it, before it is given a label.
/// </summary>
/// <typeparam name="TKey">The grouping key, usually an anonymous type of key columns.</typeparam>
internal sealed record AggregateRow<TKey>
    where TKey : notnull
{
    public TKey Key { get; init; } = default!;

    public long Turns { get; init; }

    public long CompletedTurns { get; init; }

    public long CancelledTurns { get; init; }

    public long FailedTurns { get; init; }

    public long NamingTurns { get; init; }

    public long NamingTokens { get; init; }

    public long AssistantTokens { get; init; }

    public long ToolTokens { get; init; }

    public long TotalTokens { get; init; }

    public long ContextTokens { get; init; }

    /// <summary>
    /// What the priced calls in this group cost, as the database added them up.
    /// </summary>
    /// <remarks>
    /// Not the figure to report. Read <see cref="EstimatedCost"/> instead, which restores the
    /// distinction this one loses.
    /// </remarks>
    public decimal? PricedCost { get; init; }

    public long UnpricedTurns { get; init; }

    /// <summary>
    /// What the group cost, or <see langword="null"/> when nothing in it could be priced.
    /// </summary>
    /// <remarks>
    /// Derived rather than read straight off the aggregate, because the provider does not preserve
    /// the distinction on its own: SQL Server sums an all-NULL set to NULL, but EF wraps the
    /// aggregate in a COALESCE that turns that into zero — and zero reads as free where the honest
    /// answer is unpriceable. The comparison below restores it from a count the same query already
    /// produced, so it costs no extra work and cannot disagree with what was summed.
    /// </remarks>
    public decimal? EstimatedCost => UnpricedTurns == Turns ? null : PricedCost;
}
