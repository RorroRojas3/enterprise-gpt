using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Agents.Models;

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
