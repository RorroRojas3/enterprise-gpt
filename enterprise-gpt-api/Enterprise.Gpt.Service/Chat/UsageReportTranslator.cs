using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Agents;

namespace Enterprise.Gpt.Service.Chat;

/// <summary>
/// What a finished chat request cost, in the shape the audit tables store it.
/// </summary>
/// <param name="InputTokens">Input tokens consumed by the assistant's own model turns.</param>
/// <param name="OutputTokens">Output tokens produced by the assistant's own model turns.</param>
/// <param name="ToolInputTokens">Input tokens consumed by every tool the request invoked.</param>
/// <param name="ToolOutputTokens">Output tokens produced by every tool the request invoked.</param>
/// <param name="CachedInputTokens">
/// Input tokens the assistant's turns served from the provider's prompt cache, already counted
/// inside <paramref name="InputTokens"/>. <see langword="null"/> when nothing reported one, which
/// is not the same as a cache miss.
/// </param>
/// <param name="ReasoningTokens">
/// Reasoning tokens the assistant's turns produced, already counted inside
/// <paramref name="OutputTokens"/>, on the same null terms.
/// </param>
/// <param name="Turns">
/// What the provider reported for each model turn, in report order. <b>Empty for a request that
/// did not stream</b> — providers report one aggregate then and the middleware surfaces no
/// breakdown — which is not the same as a request that had no turns.
/// </param>
/// <param name="ToolCalls">
/// Every tool invocation the request made, flat and in pre-order, with nesting expressed through
/// each row's <see cref="ConversationUsageToolCall.Children"/>.
/// </param>
/// <remarks>
/// <see cref="ToolCalls"/> is flat rather than a forest of roots because it is assigned straight to
/// <see cref="ConversationUsage.ToolCalls"/>: that navigation is what tells EF each row belongs to
/// the audit row, and a nested call reachable only through its parent would be saved with no owning
/// usage id at all. Roots are the entries whose
/// <see cref="ConversationUsageToolCall.Depth"/> is zero.
/// </remarks>
internal sealed record TurnUsage(
    long InputTokens,
    long OutputTokens,
    long ToolInputTokens,
    long ToolOutputTokens,
    long? CachedInputTokens,
    long? ReasoningTokens,
    List<ConversationUsageToolCall> ToolCalls,
    List<ConversationUsageTurn> Turns)
{
    /// <summary>
    /// Creates an empty result, for a request that produced no report at all.
    /// </summary>
    /// <returns>A result with no tokens, no tool calls and no turns.</returns>
    /// <remarks>
    /// A method rather than a shared instance: <see cref="ToolCalls"/> and <see cref="Turns"/> are
    /// assigned straight into EF navigations, which the change tracker then owns and mutates in
    /// place, so handing out the same list twice would let one turn's fixup append entities to
    /// another turn's graph.
    /// </remarks>
    public static TurnUsage Empty()
    {
        return new TurnUsage(
            InputTokens: 0,
            OutputTokens: 0,
            ToolInputTokens: 0,
            ToolOutputTokens: 0,
            CachedInputTokens: null,
            ReasoningTokens: null,
            ToolCalls: [],
            Turns: []);
    }

    /// <summary>
    /// Every input token the request consumed: the assistant's own turns plus its tools.
    /// </summary>
    /// <remarks>
    /// Derived from the same numbers written to the audit row rather than read separately off the
    /// report, so a conversation's running counters can never disagree with the sum of its usage
    /// rows.
    /// </remarks>
    public long TotalInputTokens => InputTokens + ToolInputTokens;

    /// <summary>
    /// Every output token the request produced, on the same terms as <see cref="TotalInputTokens"/>.
    /// </summary>
    public long TotalOutputTokens => OutputTokens + ToolOutputTokens;
}

/// <summary>
/// Turns the tool-tracking middleware's <see cref="ChatUsageReport"/> into the audit rows that
/// record it.
/// </summary>
internal static class UsageReportTranslator
{
    /// <summary>
    /// Translates a completed request's usage report into the assistant/tool token split and the
    /// tree of tool-call rows underneath it.
    /// </summary>
    /// <param name="report">
    /// The report the middleware produced, or <see langword="null"/> when the request produced
    /// none.
    /// </param>
    /// <param name="mcpServers">
    /// The MCP servers leased for the request, used to attribute MCP tool calls back to a catalog
    /// row.
    /// </param>
    /// <param name="dateCreated">The timestamp stamped on every row, shared with the parent call.</param>
    /// <param name="fileAgent">
    /// The File Agent's catalog row when the turn attached it, or <see langword="null"/> when it did
    /// not — which is every turn that never offered the agent.
    /// </param>
    /// <returns>The token split and the tool-call rows; an empty result for a null report.</returns>
    public static TurnUsage Translate(
        ChatUsageReport? report,
        IReadOnlyList<McpServerReference> mcpServers,
        DateTimeOffset dateCreated,
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
        BuildToolCalls(report.ToolCalls, parentDepth: -1, prefixes, dateCreated, fileAgent, flattened);

        // The top-level entries already roll their descendants up, so summing them is summing
        // everything every tool consumed.
        long toolInput = 0, toolOutput = 0;
        foreach (var call in report.ToolCalls)
        {
            toolInput += call.Usage?.InputTokenCount ?? 0;
            toolOutput += call.Usage?.OutputTokenCount ?? 0;
        }

        var turns = BuildTurns(report.Turns, dateCreated);

        return new TurnUsage(
            InputTokens: report.AssistantUsage.InputTokenCount ?? 0,
            OutputTokens: report.AssistantUsage.OutputTokenCount ?? 0,
            ToolInputTokens: toolInput,
            ToolOutputTokens: toolOutput,
            // Summed from the turns rather than read off AssistantUsage, so this reports the same
            // number whichever of the two the middleware ends up carrying.
            //
            // Both are null through Andes 0.7.0 whatever the provider reported. Its usage arithmetic
            // copies input, output, total and the AdditionalCounts dictionary and nothing else, and
            // every UsageDetails on the report — the assistant aggregate and each turn alike — is
            // produced by that copy. These two counts became first-class properties on UsageDetails
            // after it was written, so they are dropped in transit. The columns are written anyway:
            // they are correct the day the middleware carries the values, and a null that means
            // "not reported" is the same null either way.
            CachedInputTokens: SumReported(turns, turn => turn.CachedInputTokens),
            ReasoningTokens: SumReported(turns, turn => turn.ReasoningTokens),
            ToolCalls: flattened,
            Turns: turns);
    }

    /// <summary>
    /// Builds one row per model turn the report broke the request into.
    /// </summary>
    /// <returns>
    /// The rows, or an empty list for a request with no breakdown — a non-streamed call, whose
    /// provider reports a single aggregate instead.
    /// </returns>
    private static List<ConversationUsageTurn> BuildTurns(
        IReadOnlyList<AssistantTurnUsage> turns,
        DateTimeOffset dateCreated)
    {
        List<ConversationUsageTurn> rows = new(turns.Count);

        foreach (var turn in turns)
        {
            rows.Add(new ConversationUsageTurn
            {
                Iteration = turn.Iteration,
                ResponseId = Truncate(turn.ResponseId, ConversationUsageTurn.ResponseIdMaxLength),
                ReportedModelId = Truncate(turn.ModelId, ConversationUsageTurn.ReportedModelIdMaxLength),
                InputTokens = turn.Usage?.InputTokenCount,
                OutputTokens = turn.Usage?.OutputTokenCount,
                TotalTokens = turn.Usage?.TotalTokenCount,
                CachedInputTokens = turn.Usage?.CachedInputTokenCount,
                ReasoningTokens = turn.Usage?.ReasoningTokenCount,
                DateCreated = dateCreated
            });
        }

        return rows;
    }

    /// <summary>
    /// Adds up one count across every turn that reported it.
    /// </summary>
    /// <returns>
    /// The sum, or <see langword="null"/> when no turn reported the count at all — which says the
    /// provider was silent about it rather than that it was zero.
    /// </returns>
    /// <remarks>
    /// A turn that reported nothing contributes nothing rather than blocking the sum, so when only
    /// some turns report, the result is a <em>partial</em> total that no column distinguishes from
    /// a complete one. That is the lesser evil: the alternative is discarding counts the provider
    /// did report. A reader who needs to know whether a figure is complete has to compare against
    /// the turn rows, which is why they are stored.
    /// </remarks>
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
    /// Builds the rows for one level of the tree, appending every row it creates — at this level
    /// and below — to <paramref name="flattened"/>.
    /// </summary>
    /// <returns>Just this level's rows, for the caller to hang off its own children.</returns>
    private static List<ConversationUsageToolCall> BuildToolCalls(
        IReadOnlyList<ToolCallUsage> calls,
        int parentDepth,
        List<(Guid Id, string Prefix, string Name)> prefixes,
        DateTimeOffset dateCreated,
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
                // A nested call reports the iteration of the outer turn that issued its enclosing
                // root call, so this is copied rather than derived, and grouping by it has to
                // restrict to depth zero or a subtree counts once per level.
                Iteration = call.Iteration,
                Kind = kind,
                ToolName = Truncate(call.ToolName, ConversationUsageToolCall.ToolNameMaxLength) ?? string.Empty,
                Source = Truncate(call.Source, ConversationUsageToolCall.SourceMaxLength),
                CallId = Truncate(call.CallId, ConversationUsageToolCall.CallIdMaxLength),
                McpServerId = kind == ConversationToolKinds.McpTool
                    ? ResolveMcpServerId(call, prefixes)
                    : null,
                // Only an agent this application composed can name the model behind it, and only for
                // its own root call: an MCP server is opaque, a function that reports usage does not
                // say what produced it, and an agent's own nested calls are the agent's tools rather
                // than further model turns. Every other row still writes null, and that is not a gap
                // to be filled later — it is the honest answer.
                ModelId = agentModel?.ModelId,
                DeploymentName = Truncate(agentModel?.DeploymentName, ConversationUsageToolCall.DeploymentNameMaxLength),
                InputTokens = OwnTokens(call.Usage?.InputTokenCount, call.Children, child => child.Usage?.InputTokenCount),
                OutputTokens = OwnTokens(call.Usage?.OutputTokenCount, call.Children, child => child.Usage?.OutputTokenCount),
                TotalTokens = OwnTokens(call.Usage?.TotalTokenCount, call.Children, child => child.Usage?.TotalTokenCount),
                SubtreeTotalTokens = call.Usage?.TotalTokenCount,
                DurationMs = (long)Math.Round(call.Duration.TotalMilliseconds),
                Succeeded = call.Succeeded,
                DateCreated = dateCreated
            };

            // Appended before its own children so the flat list stays in pre-order, which is the
            // order a reader rebuilding the tree wants and the order the tests read it back in.
            rows.Add(row);
            flattened.Add(row);

            row.Children = BuildToolCalls(call.Children, depth, prefixes, dateCreated, fileAgent, flattened);
        }

        return rows;
    }

    /// <summary>
    /// Matches an agent-kind call to the one agent this application composes.
    /// </summary>
    /// <returns>
    /// The catalog row, or <see langword="null"/> for every other call — and for a File Agent call on a
    /// turn that carries no resolved row, which is a misconfiguration the startup validator should have
    /// caught. Null rather than a guess: the column is a foreign key, and this write runs in a
    /// <c>finally</c> after the answer has already streamed.
    /// </returns>
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
    /// A null rollup means nothing was attributed, which is not the same as zero, so it stays null.
    /// A null child rollup contributes nothing to the subtraction. The result is floored at zero:
    /// the middleware computes the rollup as own-plus-children, so a negative could only come from
    /// a defect upstream, and letting one reach a column other queries <c>SUM</c> would corrupt
    /// totals far from where it originated.
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

        // Fallback for a tool that reached the model by some other path. McpToolProvider now hands
        // the tracking wrapper the catalog name, so a tool this application leased matches here
        // exactly rather than by luck; anything reaching the model another way still reports
        // whatever name its own wrapper carries, which is why the comparison stays case-insensitive
        // and why a miss leaves the id null rather than guessing.
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
    /// Clips a value to its column width. The audit write runs in a <c>finally</c> after the answer
    /// has already been streamed, so a tool advertising an unusually long name must not be able to
    /// turn the whole turn's accounting into a truncation error.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
