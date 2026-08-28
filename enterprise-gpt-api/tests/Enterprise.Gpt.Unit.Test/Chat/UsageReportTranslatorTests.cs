using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Chat;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Chat;

/// <summary>
/// Covers the translation of a <see cref="ChatUsageReport"/> into audit rows: the assistant/tool
/// token split, the own-versus-subtree arithmetic that keeps aggregates from double-counting nested
/// calls, and the attribution of MCP calls back to a catalog server.
/// </summary>
public sealed class UsageReportTranslatorTests
{
    private static readonly DateTimeOffset _date = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly FileAgentModel _fileAgent =
        new(Guid.Parse("2f0d0c1e-6c0a-4f0a-9b3f-2a1c9a5f7e01"), Providers.AzureOpenAI, "gpt-file-agent");

    private static UsageDetails Usage(
        long input,
        long output,
        long? total = null,
        long? cached = null,
        long? reasoning = null)
    {
        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = total ?? input + output,
            CachedInputTokenCount = cached,
            ReasoningTokenCount = reasoning
        };
    }

    private static ChatUsageReport Report(UsageDetails assistant, params ToolCallUsage[] toolCalls)
    {
        return new ChatUsageReport
        {
            AssistantUsage = assistant,
            ToolCalls = toolCalls,
            TotalUsage = assistant
        };
    }

    private static ChatUsageReport ReportWithTurns(
        UsageDetails assistant,
        AssistantTurnUsage[] turns,
        params ToolCallUsage[] toolCalls)
    {
        return new ChatUsageReport
        {
            AssistantUsage = assistant,
            ToolCalls = toolCalls,
            Turns = turns,
            TotalUsage = assistant
        };
    }

    private static AssistantTurnUsage Turn(
        int iteration,
        // Non-null on the middleware's own type, unlike the two identifiers beside it, so a turn
        // that reported nothing is expressed as a UsageDetails whose counts are null rather than as
        // an absent one.
        UsageDetails? usage = null,
        string? responseId = null,
        string? modelId = null)
    {
        return new AssistantTurnUsage
        {
            Iteration = iteration,
            Usage = usage ?? new UsageDetails(),
            ResponseId = responseId,
            ModelId = modelId
        };
    }

    private static ToolCallUsage Call(
        string toolName,
        UsageDetails? usage = null,
        ToolKind kind = ToolKind.Function,
        string? source = null,
        string? callId = null,
        bool succeeded = true,
        TimeSpan duration = default,
        int iteration = 0,
        params ToolCallUsage[] children)
    {
        return new ToolCallUsage
        {
            ToolName = toolName,
            Kind = kind,
            Source = source,
            CallId = callId,
            Usage = usage,
            Succeeded = succeeded,
            Duration = duration,
            Iteration = iteration,
            Children = children
        };
    }

    private static List<ConversationUsageToolCall> Roots(TurnUsage usage)
    {
        return [.. usage.ToolCalls.Where(call => call.Depth == 0)];
    }

    [Fact]
    public void Translate_NullReport_ReturnsEmpty()
    {
        var result = UsageReportTranslator.Translate(null, [], _date);

        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
        Assert.Equal(0, result.ToolInputTokens);
        Assert.Equal(0, result.ToolOutputTokens);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public void Translate_NoToolCalls_SplitsAssistantTokensOnly()
    {
        var result = UsageReportTranslator.Translate(Report(Usage(11, 7)), [], _date);

        Assert.Equal(11, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Equal(0, result.ToolInputTokens);
        Assert.Equal(0, result.ToolOutputTokens);
        Assert.Equal(11, result.TotalInputTokens);
        Assert.Equal(7, result.TotalOutputTokens);
    }

    [Fact]
    public void Translate_TopLevelToolCalls_SumsTheirUsageIntoTheToolColumns()
    {
        var report = Report(Usage(11, 7), Call("GetWeather", Usage(3, 2)), Call("GetTraffic", Usage(4, 1)));

        var result = UsageReportTranslator.Translate(report, [], _date);

        Assert.Equal(11, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Equal(7, result.ToolInputTokens);
        Assert.Equal(3, result.ToolOutputTokens);
        Assert.Equal(18, result.TotalInputTokens);
        Assert.Equal(10, result.TotalOutputTokens);
    }

    // Every row is returned flat, nested ones included, because that list is assigned straight to
    // the audit row's navigation — a call reachable only through its parent would be saved with no
    // owning usage id, which SQL Server rejects and SQLite does not.
    [Fact]
    public void Translate_NestedToolCalls_ReturnsEveryRowFlatInPreOrder()
    {
        var grandchild = Call("Lookup", Usage(5, 5));
        var child = Call("InnerAgent", Usage(30, 20), kind: ToolKind.Agent, children: grandchild);
        var report = Report(
            Usage(0, 0),
            Call("OuterAgent", Usage(100, 60), kind: ToolKind.Agent, children: child),
            Call("Sibling", Usage(1, 1)));

        var rows = UsageReportTranslator.Translate(report, [], _date).ToolCalls;

        Assert.Equal(["OuterAgent", "InnerAgent", "Lookup", "Sibling"], rows.Select(r => r.ToolName));
    }

    [Fact]
    public void Translate_ToolCall_CopiesItsIdentityAndOutcome()
    {
        var report = Report(
            Usage(0, 0),
            Call("GetWeather", Usage(3, 2), callId: "call_1", succeeded: false, duration: TimeSpan.FromMilliseconds(1500)));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date).ToolCalls);

        Assert.Equal("GetWeather", row.ToolName);
        Assert.Equal(ConversationToolKinds.Function, row.Kind);
        Assert.Equal("call_1", row.CallId);
        Assert.Equal(1500, row.DurationMs);
        Assert.False(row.Succeeded);
        Assert.Equal(0, row.Sequence);
        Assert.Equal(0, row.Depth);
        Assert.Equal(_date, row.DateCreated);
    }

    [Theory]
    [InlineData(ToolKind.Function, ConversationToolKinds.Function)]
    [InlineData(ToolKind.McpTool, ConversationToolKinds.McpTool)]
    [InlineData(ToolKind.Agent, ConversationToolKinds.Agent)]
    [InlineData(ToolKind.Unknown, ConversationToolKinds.Unknown)]
    public void Translate_ToolKind_MapsToTheMatchingPersistedKind(ToolKind kind, ConversationToolKinds expected)
    {
        var report = Report(Usage(0, 0), Call("Anything", Usage(1, 1), kind: kind));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date).ToolCalls);

        Assert.Equal(expected, row.Kind);
    }

    [Fact]
    public void Translate_SiblingToolCalls_NumbersThemInReportOrder()
    {
        var report = Report(Usage(0, 0), Call("First", Usage(1, 1)), Call("Second", Usage(1, 1)));

        var rows = UsageReportTranslator.Translate(report, [], _date).ToolCalls;

        Assert.Equal(["First", "Second"], rows.Select(r => r.ToolName));
        Assert.Equal([0, 1], rows.Select(r => r.Sequence));
    }

    // The invariant the column split exists for: the middleware reports a rollup, so persisting it
    // on every row would count a nested agent's tokens once for the child and again for the parent.
    [Fact]
    public void Translate_NestedToolCall_StoresOwnTokensOnTheParentAndTheRollupBesideThem()
    {
        var child = Call("InnerSearch", Usage(30, 20), kind: ToolKind.Function);
        var report = Report(Usage(11, 7), Call("ResearchAgent", Usage(100, 60), kind: ToolKind.Agent, children: child));

        var result = UsageReportTranslator.Translate(report, [], _date);

        var parent = Assert.Single(Roots(result));
        Assert.Equal(70, parent.InputTokens);
        Assert.Equal(40, parent.OutputTokens);
        Assert.Equal(110, parent.TotalTokens);
        Assert.Equal(160, parent.SubtreeTotalTokens);

        var persistedChild = Assert.Single(parent.Children);
        Assert.Equal(30, persistedChild.InputTokens);
        Assert.Equal(20, persistedChild.OutputTokens);
        Assert.Equal(50, persistedChild.SubtreeTotalTokens);
        Assert.Equal(1, persistedChild.Depth);

        // Summing the own-token columns across the whole tree reproduces the rollup exactly once.
        Assert.Equal(parent.SubtreeTotalTokens, parent.TotalTokens + persistedChild.SubtreeTotalTokens);

        // The parent's rollup is what the call cost the conversation, so that is what the audit row
        // carries — not the parent's own consumption.
        Assert.Equal(100, result.ToolInputTokens);
        Assert.Equal(60, result.ToolOutputTokens);
    }

    [Fact]
    public void Translate_TwoLevelsOfNesting_KeepsDepthAndTheRollupInvariantAtEveryLevel()
    {
        var grandchild = Call("Lookup", Usage(5, 5));
        var child = Call("InnerAgent", Usage(30, 20), kind: ToolKind.Agent, children: grandchild);
        var report = Report(Usage(0, 0), Call("OuterAgent", Usage(100, 60), kind: ToolKind.Agent, children: child));

        var outer = Assert.Single(Roots(UsageReportTranslator.Translate(report, [], _date)));
        var inner = Assert.Single(outer.Children);
        var leaf = Assert.Single(inner.Children);

        Assert.Equal([0, 1, 2], new[] { outer.Depth, inner.Depth, leaf.Depth });
        Assert.Equal(10, leaf.TotalTokens);
        Assert.Equal(40, inner.TotalTokens);
        Assert.Equal(50, inner.SubtreeTotalTokens);
        Assert.Equal(110, outer.TotalTokens);
        Assert.Equal(160, outer.SubtreeTotalTokens);
    }

    // Nothing attributed is not the same as zero attributed: an MCP server need not report what it
    // spent, and recording a zero would assert something the middleware never said.
    [Fact]
    public void Translate_ToolCallWithNoUsage_LeavesTheTokenColumnsNull()
    {
        var report = Report(Usage(11, 7), Call("Opaque", usage: null, kind: ToolKind.McpTool));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date).ToolCalls);

        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.TotalTokens);
        Assert.Null(row.SubtreeTotalTokens);
    }

    [Fact]
    public void Translate_ChildReportsNothing_LeavesTheParentsOwnTokensEqualToItsRollup()
    {
        var child = Call("Opaque", usage: null, kind: ToolKind.McpTool);
        var report = Report(Usage(0, 0), Call("Agent", Usage(40, 10), kind: ToolKind.Agent, children: child));

        var parent = Assert.Single(Roots(UsageReportTranslator.Translate(report, [], _date)));

        Assert.Equal(40, parent.InputTokens);
        Assert.Equal(10, parent.OutputTokens);
        Assert.Equal(50, parent.TotalTokens);
    }

    // A rollup smaller than its children could only come from a defect upstream, and a negative in
    // a column other queries SUM would corrupt totals far from where it originated.
    [Fact]
    public void Translate_ChildRollupExceedsTheParents_FloorsTheParentsOwnTokensAtZero()
    {
        var child = Call("Inner", Usage(100, 100));
        var report = Report(Usage(0, 0), Call("Outer", Usage(10, 10), kind: ToolKind.Agent, children: child));

        var parent = Assert.Single(Roots(UsageReportTranslator.Translate(report, [], _date)));

        Assert.Equal(0, parent.InputTokens);
        Assert.Equal(0, parent.OutputTokens);
        Assert.Equal(0, parent.TotalTokens);
    }

    [Fact]
    public void Translate_McpToolNamedWithItsServerPrefix_AttributesItToThatServer()
    {
        var weather = new McpServerReference(Guid.NewGuid(), "Weather");
        var tickets = new McpServerReference(Guid.NewGuid(), "Tickets");
        var report = Report(Usage(0, 0), Call("Weather_forecast", Usage(1, 1), kind: ToolKind.McpTool, source: "anything"));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [weather, tickets], _date).ToolCalls);

        Assert.Equal(weather.Id, row.McpServerId);
    }

    // Server names are sanitized into the prefix, so a name with spaces still has to correlate.
    [Fact]
    public void Translate_McpServerNameNeedingSanitizing_StillAttributesByPrefix()
    {
        var server = new McpServerReference(Guid.NewGuid(), "Andes Test MCP");
        var report = Report(Usage(0, 0), Call("Andes_Test_MCP_echo", Usage(1, 1), kind: ToolKind.McpTool));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [server], _date).ToolCalls);

        Assert.Equal(server.Id, row.McpServerId);
    }

    // One prefix being a prefix of another must not steal the more specific server's tools.
    [Fact]
    public void Translate_OneServerPrefixExtendsAnother_PrefersTheLongerMatch()
    {
        var shorter = new McpServerReference(Guid.NewGuid(), "Weather");
        var longer = new McpServerReference(Guid.NewGuid(), "Weather_Pro");
        var report = Report(Usage(0, 0), Call("Weather_Pro_forecast", Usage(1, 1), kind: ToolKind.McpTool));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [shorter, longer], _date).ToolCalls);

        Assert.Equal(longer.Id, row.McpServerId);
    }

    [Fact]
    public void Translate_McpToolWithoutAPrefixMatch_FallsBackToTheReportedSourceName()
    {
        var server = new McpServerReference(Guid.NewGuid(), "Weather");
        var report = Report(Usage(0, 0), Call("forecast", Usage(1, 1), kind: ToolKind.McpTool, source: "weather"));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [server], _date).ToolCalls);

        Assert.Equal(server.Id, row.McpServerId);
    }

    // Never guessed: a report that cannot distinguish "not an MCP tool" from "we do not know" is
    // worse than one that admits the gap.
    [Fact]
    public void Translate_McpToolMatchingNothing_LeavesTheServerUnattributed()
    {
        var server = new McpServerReference(Guid.NewGuid(), "Weather");
        var report = Report(Usage(0, 0), Call("forecast", Usage(1, 1), kind: ToolKind.McpTool, source: "Elsewhere"));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [server], _date).ToolCalls);

        Assert.Null(row.McpServerId);
    }

    [Fact]
    public void Translate_FunctionToolNamedLikeAnMcpTool_IsNotAttributedToAServer()
    {
        var server = new McpServerReference(Guid.NewGuid(), "Weather");
        var report = Report(Usage(0, 0), Call("Weather_forecast", Usage(1, 1), kind: ToolKind.Function));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [server], _date).ToolCalls);

        Assert.Null(row.McpServerId);
    }

    // Nothing in a tool call names the model behind it, and inventing one would make the report
    // assert something no component observed.
    [Fact]
    public void Translate_AnyToolCall_LeavesTheModelUnattributed()
    {
        var report = Report(Usage(0, 0), Call("ResearchAgent", Usage(1, 1), kind: ToolKind.Agent, source: "Research Agent"));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date).ToolCalls);

        Assert.Null(row.ModelId);
        Assert.Null(row.DeploymentName);
        Assert.Equal("Research Agent", row.Source);
    }

    // The audit write runs in a finally after the answer has already been streamed, so an unusually
    // long name must not turn the turn's accounting into a truncation error.
    [Fact]
    public void Translate_OversizedNames_ClipsThemToTheirColumnWidths()
    {
        var report = Report(
            Usage(0, 0),
            Call(new string('t', 400), Usage(1, 1), source: new string('s', 400), callId: new string('c', 200)));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date).ToolCalls);

        Assert.Equal(256, row.ToolName.Length);
        Assert.Equal(256, row.Source!.Length);
        Assert.Equal(128, row.CallId!.Length);
    }

    // The per-turn breakdown is what makes a tool's prompt cost attributable: the cost of the calls
    // one turn issued is the growth of the prompt between that turn and the next, and neither term
    // exists without these rows.
    [Fact]
    public void Translate_ReportWithTurns_RecordsOneRowPerTurnInReportOrder()
    {
        var report = ReportWithTurns(
            Usage(16, 10),
            [
                Turn(0, Usage(5, 3), responseId: "resp-0"),
                Turn(1, Usage(11, 7), responseId: "resp-1")
            ],
            Call("Weather_forecast", Usage(0, 0), kind: ToolKind.McpTool));

        var turns = UsageReportTranslator.Translate(report, [], _date).Turns;

        Assert.Equal([0, 1], turns.Select(turn => turn.Iteration));
        Assert.Equal(["resp-0", "resp-1"], turns.Select(turn => turn.ResponseId));
        Assert.Equal([5, 11], turns.Select(turn => turn.InputTokens));
        Assert.Equal([3, 7], turns.Select(turn => turn.OutputTokens));
        Assert.All(turns, turn => Assert.Equal(_date, turn.DateCreated));
    }

    // The turn rows partition the assistant columns rather than adding to them. A report that
    // summed across the two would double-count every assistant token in the trail.
    [Fact]
    public void Translate_ReportWithTurns_TurnTokensSumToTheAssistantTotals()
    {
        var report = ReportWithTurns(Usage(16, 10), [Turn(0, Usage(5, 3)), Turn(1, Usage(11, 7))]);

        var result = UsageReportTranslator.Translate(report, [], _date);

        Assert.Equal(result.InputTokens, result.Turns.Sum(turn => turn.InputTokens ?? 0));
        Assert.Equal(result.OutputTokens, result.Turns.Sum(turn => turn.OutputTokens ?? 0));
    }

    // A non-streamed request reports one aggregate and no breakdown. Synthesising a turn from that
    // aggregate would manufacture an iteration nobody reported and pair it with the zero the
    // non-streaming path stamps on every tool call, producing a delta out of thin air.
    [Fact]
    public void Translate_ReportWithoutTurns_RecordsNoTurnRows()
    {
        var report = Report(Usage(11, 7), Call("forecast", Usage(1, 1)));

        var result = UsageReportTranslator.Translate(report, [], _date);

        Assert.Empty(result.Turns);
        Assert.Equal(11, result.InputTokens);
    }

    // A provider that reports a usage block with nothing in it is saying it did not count, which is
    // not the same as counting zero — and only one of those belongs in a cost report.
    [Fact]
    public void Translate_TurnReportingNoCounts_LeavesThemNullRatherThanZero()
    {
        var report = ReportWithTurns(Usage(0, 0), [Turn(0, new UsageDetails())]);

        var turn = Assert.Single(UsageReportTranslator.Translate(report, [], _date).Turns);

        Assert.Null(turn.InputTokens);
        Assert.Null(turn.OutputTokens);
        Assert.Null(turn.TotalTokens);
    }

    // Grouping tool calls by iteration is the whole point of the column, so it has to survive the
    // flattening that Depth and Sequence already survive.
    [Fact]
    public void Translate_ToolCalls_CarryTheIterationThatIssuedThem()
    {
        var report = ReportWithTurns(
            Usage(0, 0),
            [Turn(0, Usage(5, 3)), Turn(1, Usage(11, 7))],
            Call("first", Usage(1, 1), iteration: 0),
            Call("second", Usage(1, 1), iteration: 1));

        var rows = UsageReportTranslator.Translate(report, [], _date).ToolCalls;

        Assert.Equal([0, 1], rows.Select(row => row.Iteration));
    }

    // A nested call reports the iteration of the outer turn that issued its enclosing root, not one
    // of its own — which is exactly why a delta query has to restrict to depth zero.
    [Fact]
    public void Translate_NestedToolCall_CarriesTheEnclosingRootsIteration()
    {
        var child = Call("child", Usage(1, 1), iteration: 2);
        var report = ReportWithTurns(
            Usage(0, 0),
            [Turn(0, Usage(5, 3))],
            Call("parent", Usage(4, 4), kind: ToolKind.Agent, iteration: 2, children: child));

        var rows = UsageReportTranslator.Translate(report, [], _date).ToolCalls;

        Assert.Equal([0, 1], rows.Select(row => row.Depth));
        Assert.All(rows, row => Assert.Equal(2, row.Iteration));
    }

    // Same reasoning as the tool-name clip: this write runs after the answer has streamed, so an
    // unusually long provider identifier must not fail the turn's accounting.
    [Fact]
    public void Translate_OversizedTurnIdentifiers_ClipsThemToTheirColumnWidths()
    {
        var report = ReportWithTurns(
            Usage(0, 0),
            [Turn(0, Usage(1, 1), responseId: new string('r', 400), modelId: new string('m', 800))]);

        var turn = Assert.Single(UsageReportTranslator.Translate(report, [], _date).Turns);

        Assert.Equal(ConversationUsageTurn.ResponseIdMaxLength, turn.ResponseId!.Length);
        Assert.Equal(ConversationUsageTurn.ReportedModelIdMaxLength, turn.ReportedModelId!.Length);
    }

    // Both counts are billed subsets of their parent column, and both are dropped by the
    // middleware's aggregation today — so this pins the wiring, not the middleware.
    [Fact]
    public void Translate_TurnsReportingDetailCounts_SumsThemOntoTheRow()
    {
        var report = ReportWithTurns(
            Usage(16, 10),
            [
                Turn(0, Usage(5, 3, cached: 2, reasoning: 1)),
                Turn(1, Usage(11, 7, cached: 4, reasoning: 3))
            ]);

        var result = UsageReportTranslator.Translate(report, [], _date);

        Assert.Equal(6, result.CachedInputTokens);
        Assert.Equal(4, result.ReasoningTokens);
    }

    // The branch between the two below: a non-null figure that is silently partial. Worth pinning
    // because nothing on the row says so — only the turn rows do.
    [Fact]
    public void Translate_SomeTurnsReportingDetailCounts_SumsOnlyWhatWasReported()
    {
        var report = ReportWithTurns(
            Usage(16, 10),
            [Turn(0, Usage(5, 3, cached: 2)), Turn(1, Usage(11, 7))]);

        var result = UsageReportTranslator.Translate(report, [], _date);

        Assert.Equal(2, result.CachedInputTokens);
        Assert.Null(result.ReasoningTokens);
    }

    // Null is not zero: no provider reporting a cache figure is a different fact from a cache miss,
    // and only one of them should reach a cost report.
    [Fact]
    public void Translate_TurnsReportingNoDetailCounts_LeavesThemNull()
    {
        var report = ReportWithTurns(Usage(16, 10), [Turn(0, Usage(5, 3)), Turn(1, Usage(11, 7))]);

        var result = UsageReportTranslator.Translate(report, [], _date);

        Assert.Null(result.CachedInputTokens);
        Assert.Null(result.ReasoningTokens);
    }

    [Fact]
    public void Translate_FileAgentCall_RecordsTheAgentsCatalogModel()
    {
        var report = Report(Usage(10, 4), Call(FileAgentToolNames.Agent, Usage(30, 12), ToolKind.Agent));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date, _fileAgent).ToolCalls);

        Assert.Equal(ConversationToolKinds.Agent, row.Kind);
        Assert.Equal(_fileAgent.ModelId, row.ModelId);
        Assert.Equal(_fileAgent.DeploymentName, row.DeploymentName);
    }

    // The agent's own nested calls are its tools, not further model turns of its own, so attributing
    // the agent's model to them would claim a cost for a row that never made a model call.
    [Fact]
    public void Translate_CallsBeneathTheFileAgent_RecordNoModel()
    {
        var report = Report(
            Usage(10, 4),
            Call(FileAgentToolNames.Agent, Usage(30, 12), ToolKind.Agent, children: Call("load_skill", Usage(4, 1))));

        var child = Assert.Single(
            UsageReportTranslator.Translate(report, [], _date, _fileAgent).ToolCalls, call => call.Depth == 1);

        Assert.Null(child.ModelId);
        Assert.Null(child.DeploymentName);
    }

    [Theory]
    [InlineData(ToolKind.Function, "document_search")]
    [InlineData(ToolKind.McpTool, "jira_search")]
    [InlineData(ToolKind.Agent, "some_other_agent")]
    public void Translate_EveryOtherCall_RecordsNoModel(ToolKind kind, string toolName)
    {
        var report = Report(Usage(10, 4), Call(toolName, Usage(3, 1), kind));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date, _fileAgent).ToolCalls);

        Assert.Null(row.ModelId);
        Assert.Null(row.DeploymentName);
    }

    // The misconfiguration the startup validator is meant to catch. The row is still written, without
    // a model, rather than failing a save that runs after the answer has already streamed.
    [Fact]
    public void Translate_FileAgentCallWithNoResolvedModel_LeavesTheColumnsNull()
    {
        var report = Report(Usage(10, 4), Call(FileAgentToolNames.Agent, Usage(30, 12), ToolKind.Agent));

        var row = Assert.Single(UsageReportTranslator.Translate(report, [], _date).ToolCalls);

        Assert.Null(row.ModelId);
        Assert.Null(row.DeploymentName);
    }
}
