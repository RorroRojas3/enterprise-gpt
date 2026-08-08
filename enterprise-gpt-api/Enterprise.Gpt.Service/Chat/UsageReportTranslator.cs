using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Chat;

/// <summary>
/// What a finished chat request cost, in the shape the audit tables store it.
/// </summary>
/// <param name="InputTokens">Input tokens consumed by the assistant's own model turns.</param>
/// <param name="OutputTokens">Output tokens produced by the assistant's own model turns.</param>
/// <param name="ToolInputTokens">Input tokens consumed by every tool the request invoked.</param>
/// <param name="ToolOutputTokens">Output tokens produced by every tool the request invoked.</param>
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
    List<ConversationUsageToolCall> ToolCalls)
{
    /// <summary>
    /// Creates an empty result, for a request that produced no report at all.
    /// </summary>
    /// <returns>A result with no tokens and no tool calls.</returns>
    /// <remarks>
    /// A method rather than a shared instance: <see cref="ToolCalls"/> is assigned straight into an
    /// EF navigation, which the change tracker then owns and mutates in place, so handing out the
    /// same list twice would let one turn's fixup append entities to another turn's graph.
    /// </remarks>
    public static TurnUsage Empty()
    {
        return new TurnUsage(0, 0, 0, 0, []);
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
    /// <returns>The token split and the tool-call rows; an empty result for a null report.</returns>
    public static TurnUsage Translate(
        ChatUsageReport? report,
        IReadOnlyList<McpServerReference> mcpServers,
        DateTimeOffset dateCreated)
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
        BuildToolCalls(report.ToolCalls, parentDepth: -1, prefixes, dateCreated, flattened);

        // The top-level entries already roll their descendants up, so summing them is summing
        // everything every tool consumed.
        long toolInput = 0, toolOutput = 0;
        foreach (var call in report.ToolCalls)
        {
            toolInput += call.Usage?.InputTokenCount ?? 0;
            toolOutput += call.Usage?.OutputTokenCount ?? 0;
        }

        return new TurnUsage(
            InputTokens: report.AssistantUsage.InputTokenCount ?? 0,
            OutputTokens: report.AssistantUsage.OutputTokenCount ?? 0,
            ToolInputTokens: toolInput,
            ToolOutputTokens: toolOutput,
            ToolCalls: flattened);
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
        List<ConversationUsageToolCall> flattened)
    {
        var depth = parentDepth + 1;
        List<ConversationUsageToolCall> rows = new(calls.Count);

        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            var kind = MapKind(call.Kind);

            var row = new ConversationUsageToolCall
            {
                Sequence = index,
                Depth = depth,
                Kind = kind,
                ToolName = Truncate(call.ToolName, ConversationUsageToolCall.ToolNameMaxLength) ?? string.Empty,
                Source = Truncate(call.Source, ConversationUsageToolCall.SourceMaxLength),
                CallId = Truncate(call.CallId, ConversationUsageToolCall.CallIdMaxLength),
                McpServerId = kind == ConversationToolKinds.McpTool
                    ? ResolveMcpServerId(call, prefixes)
                    : null,
                // Left unset on purpose. Nothing in a tool call names the model behind it: an MCP
                // server is opaque, and a function that reports usage does not say what produced
                // it. Only an agent this application configured could answer, and none exists yet.
                ModelId = null,
                DeploymentName = null,
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

            row.Children = BuildToolCalls(call.Children, depth, prefixes, dateCreated, flattened);
        }

        return rows;
    }

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

        // Fallback for a tool that reached the model by some other path: the middleware reports the
        // server's own advertised name, which need not match the catalog name but usually does.
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
