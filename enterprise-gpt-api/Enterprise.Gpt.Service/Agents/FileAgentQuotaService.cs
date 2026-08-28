using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// Whether a user may start another File Agent run, and why not when they may not.
/// </summary>
/// <param name="Allowed">Whether the tool should be attached to this turn.</param>
/// <param name="Explanation">
/// A sentence for the assistant to relay, or <see langword="null"/> when the run is allowed.
/// </param>
public sealed record FileAgentQuotaDecision(bool Allowed, string? Explanation)
{
    /// <summary>The decision for a deployment that configured no ceiling, and for a user under one.</summary>
    public static readonly FileAgentQuotaDecision Unbounded = new(true, null);
}

/// <summary>
/// Applies the per-user ceiling on file generation.
/// </summary>
/// <remarks>
/// Counted from the audit rows rather than a counter of its own, so the ceiling and the bill read the
/// same table. Both ceilings are opt-in; with neither set this never queries.
/// </remarks>
public interface IFileAgentQuotaService
{
    /// <summary>
    /// Decides whether the File Agent may be offered to a user's next turn.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="modelId">The agent's pinned catalog model, which is what marks its rows.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The decision, allowed when no ceiling is configured.</returns>
    Task<FileAgentQuotaDecision> CheckAsync(Guid userId, Guid modelId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class FileAgentQuotaService(
    EnterpriseGptDbContext ctx,
    IOptions<FileAgentOptions> options) : IFileAgentQuotaService
{
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IOptions<FileAgentOptions> _options = options;

    /// <inheritdoc />
    public async Task<FileAgentQuotaDecision> CheckAsync(
        Guid userId, Guid modelId, CancellationToken cancellationToken)
    {
        var settings = _options.Value;
        var maxRuns = settings.MaxRunsPerUserPerDay;
        var maxSeconds = settings.MaxSandboxSecondsPerUserPerDay;

        if (maxRuns is null && maxSeconds is null)
        {
            return FileAgentQuotaDecision.Unbounded;
        }

        // A rolling window rather than a calendar day: a UTC midnight reset lands in the middle of the
        // working afternoon somewhere, and every user should get the same length of day.
        var since = DateTimeOffset.UtcNow.AddDays(-1);

        var spent = await _ctx.ConversationUsageToolCalls
            .AsNoTracking()
            .Where(x => x.ModelId == modelId
                && x.Kind == ConversationToolKinds.Agent
                && x.Depth == 0
                && x.DateCreated >= since
                && x.ConversationUsage.UserId == userId)
            .GroupBy(_ => 1)
            .Select(group => new { Runs = group.Count(), Milliseconds = group.Sum(x => x.DurationMs) })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (spent is null)
        {
            return FileAgentQuotaDecision.Unbounded;
        }

        if (maxRuns is int runCeiling && spent.Runs >= runCeiling)
        {
            return new FileAgentQuotaDecision(
                false,
                $"This account has reached its limit of {runCeiling} generated file(s) in a day.");
        }

        if (maxSeconds is int secondCeiling && spent.Milliseconds / 1000 >= secondCeiling)
        {
            return new FileAgentQuotaDecision(
                false,
                $"This account has reached its limit of {secondCeiling} seconds of file generation in a day.");
        }

        return FileAgentQuotaDecision.Unbounded;
    }
}
