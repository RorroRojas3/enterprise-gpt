using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Constants.Usage;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Service.Agents.Constants;
using Enterprise.Gpt.Service.Agents.Models;
using Enterprise.Gpt.Service.Conversations.Chat.Models;
using Enterprise.Gpt.Service.McpServers;
using Enterprise.Gpt.Service.McpServers.Models;

namespace Enterprise.Gpt.Service.Conversations.Chat;

/// <summary>
/// Turns the tool-tracking middleware's <see cref="ChatUsageReport"/> into the audit rows that
/// record it.
/// </summary>
internal static class UsageReportTranslator
{
    #region Private methods

    /// <summary>One row per model turn; empty for a non-streamed call, which reports one aggregate.</summary>
    private static List<ConversationUsageTurn> BuildTurns(
        IReadOnlyList<AssistantTurnUsage> turns)
    {
        List<ConversationUsageTurn> rows = new(turns.Count);

        foreach (var turn in turns)
        {
            rows.Add(new ConversationUsageTurn
            {
                Iteration = turn.Iteration,
                ResponseId = Truncate(turn.ResponseId, ConversationUsageTurnLimits.ResponseIdMaxLength),
                ReportedModelId = Truncate(turn.ModelId, ConversationUsageTurnLimits.ReportedModelIdMaxLength),
                InputTokens = turn.Usage?.InputTokenCount,
                OutputTokens = turn.Usage?.OutputTokenCount,
                TotalTokens = turn.Usage?.TotalTokenCount,
                CachedInputTokens = turn.Usage?.CachedInputTokenCount,
                ReasoningTokens = turn.Usage?.ReasoningTokenCount
            });
        }

        return rows;
    }

    /// <summary>
    /// Sums a count across the turns that reported it, or <see langword="null"/> when none did. A partial
    /// total beats discarding reported counts; the stored turn rows show whether it is complete.
    /// </summary>
    private static long? SumReported(
        List<ConversationUsageTurn> turns,
        Func<ConversationUsageTurn, long?> selector)
    {
        long? total = null;

        foreach (var turn in turns)
        {
            if (selector(turn) is long reported)
            {
                total = (total ?? 0) + reported;
            }
        }

        return total;
    }

    /// <summary>
    /// Builds one level's rows, also appending them and their descendants to <paramref name="flattened"/>.
    /// </summary>
    private static List<ConversationUsageToolCall> BuildToolCalls(
        IReadOnlyList<ToolCallUsage> calls,
        int parentDepth,
        List<(Guid Id, string Prefix, string Name)> prefixes,
        FileAgentModel? fileAgent,
        List<ConversationUsageToolCall> flattened)
    {
        var depth = parentDepth + 1;
        List<ConversationUsageToolCall> rows = new(calls.Count);

        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            var kind = MapKind(call.Kind);
            var agentModel = ResolveAgentModel(call, kind, fileAgent);

            var row = new ConversationUsageToolCall
            {
                Sequence = index,
                Depth = depth,
                // A nested call carries its root call's outer-turn iteration, so grouping by it must
                // restrict to depth zero or a subtree counts once per level.
                Iteration = call.Iteration,
                Kind = kind,
                ToolName = Truncate(call.ToolName, ConversationUsageToolCallLimits.ToolNameMaxLength) ?? string.Empty,
                Source = Truncate(call.Source, ConversationUsageToolCallLimits.SourceMaxLength),
                CallId = Truncate(call.CallId, ConversationUsageToolCallLimits.CallIdMaxLength),
                McpServerId = kind == ConversationToolKinds.McpTool
                    ? ResolveMcpServerId(call, prefixes)
                    : null,
                // Only a composed agent's own root call can name its model; null elsewhere is the
                // honest answer (MCP servers are opaque, functions don't say), not a gap to fill.
                ModelId = agentModel?.ModelId,
                DeploymentName = Truncate(agentModel?.DeploymentName, ConversationUsageToolCallLimits.DeploymentNameMaxLength),
                InputTokens = OwnTokens(call.Usage?.InputTokenCount, call.Children, child => child.Usage?.InputTokenCount),
                OutputTokens = OwnTokens(call.Usage?.OutputTokenCount, call.Children, child => child.Usage?.OutputTokenCount),
                TotalTokens = OwnTokens(call.Usage?.TotalTokenCount, call.Children, child => child.Usage?.TotalTokenCount),
                SubtreeTotalTokens = call.Usage?.TotalTokenCount,
                DurationMs = (long)Math.Round(call.Duration.TotalMilliseconds),
                Succeeded = call.Succeeded
            };

            // Before its children, so the flat list stays in pre-order.
            rows.Add(row);
            flattened.Add(row);

            row.Children = BuildToolCalls(call.Children, depth, prefixes, fileAgent, flattened);
        }

        return rows;
    }

    /// <summary>
    /// Matches an agent-kind call to the File Agent. No resolved row yields null, not a guess: the column
    /// is a foreign key and this write runs in a <c>finally</c> after the answer streamed.
    /// </summary>
    private static FileAgentModel? ResolveAgentModel(
        ToolCallUsage call, ConversationToolKinds kind, FileAgentModel? fileAgent) =>
        kind == ConversationToolKinds.Agent
        && string.Equals(call.ToolName, FileAgentToolNames.Agent, StringComparison.OrdinalIgnoreCase)
            ? fileAgent
            : null;

    /// <summary>
    /// Reduces a reported rollup to what the invocation itself consumed.
    /// </summary>
    /// <remarks>
    /// A null rollup stays null (unattributed is not zero). Floored at zero so an upstream defect cannot
    /// corrupt totals that other queries <c>SUM</c>.
    /// </remarks>
    private static long? OwnTokens(
        long? rollup,
        IReadOnlyList<ToolCallUsage> children,
        Func<ToolCallUsage, long?> childSelector)
    {
        if (rollup is not long total)
        {
            return null;
        }

        foreach (var child in children)
        {
            total -= childSelector(child) ?? 0;
        }

        return Math.Max(0, total);
    }

    private static Guid? ResolveMcpServerId(ToolCallUsage call, List<(Guid Id, string Prefix, string Name)> prefixes)
    {
        // The tool name is the reliable key: this application stamps "{prefix}_" onto every tool it
        // leases, so the name the model called carries the server with it.
        foreach (var (Id, Prefix, Name) in prefixes)
        {
            if (call.ToolName.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return Id;
            }
        }

        // Fallback for a tool that reached the model another way: its wrapper's name may differ in
        // case, and a miss stays null rather than guessing.
        foreach (var (Id, Prefix, Name) in prefixes)
        {
            if (string.Equals(call.Source, Name, StringComparison.OrdinalIgnoreCase))
            {
                return Id;
            }
        }

        return null;
    }

    private static ConversationToolKinds MapKind(ToolKind kind) => kind switch
    {
        ToolKind.Function => ConversationToolKinds.Function,
        ToolKind.McpTool => ConversationToolKinds.McpTool,
        ToolKind.Agent => ConversationToolKinds.Agent,
        _ => ConversationToolKinds.Unknown
    };

    /// <summary>
    /// Clips to column width: the audit write runs in a <c>finally</c> after streaming, so an overlong
    /// tool name must not fail the turn's accounting.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    #endregion

    #region Public static methods

    /// <summary>
    /// Translates a usage report into the assistant/tool token split and the tool-call row tree; a null
    /// report yields an empty result.
    /// </summary>
    public static TurnUsage Translate(
        ChatUsageReport? report,
        IReadOnlyList<McpServerReference> mcpServers,
        FileAgentModel? fileAgent = null)
    {
        ArgumentNullException.ThrowIfNull(mcpServers);

        if (report is null)
        {
            return TurnUsage.Empty();
        }

        // Longest prefix first: two servers whose names sanitize to prefixes where one is a prefix
        // of the other would otherwise attribute the more specific server's tools to the shorter.
        var prefixes = mcpServers
            .Select(server => (server.Id, Prefix: $"{McpToolProvider.SanitizeToolNamePrefix(server.Name)}_", server.Name))
            .OrderByDescending(x => x.Prefix.Length)
            .ToList();

        List<ConversationUsageToolCall> flattened = [];
        BuildToolCalls(report.ToolCalls, parentDepth: -1, prefixes, fileAgent, flattened);

        // Top-level entries already roll up their descendants.
        long toolInput = 0, toolOutput = 0;
        foreach (var call in report.ToolCalls)
        {
            toolInput += call.Usage?.InputTokenCount ?? 0;
            toolOutput += call.Usage?.OutputTokenCount ?? 0;
        }

        var turns = BuildTurns(report.Turns);

        return new TurnUsage(
            InputTokens: report.AssistantUsage.InputTokenCount ?? 0,
            OutputTokens: report.AssistantUsage.OutputTokenCount ?? 0,
            ToolInputTokens: toolInput,
            ToolOutputTokens: toolOutput,
            // Summed from the turns so it matches whichever the middleware carries. Andes 0.7.0's usage
            // copy drops both counts, so they stay null until it carries them; the columns are ready.
            CachedInputTokens: SumReported(turns, turn => turn.CachedInputTokens),
            ReasoningTokens: SumReported(turns, turn => turn.ReasoningTokens),
            ToolCalls: flattened,
            Turns: turns);
    }

    #endregion
}
