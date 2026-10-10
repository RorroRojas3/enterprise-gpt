using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Persistence;

/// <summary>
/// Covers the <c>Core.ConversationUsageToolCall</c> table against a real SQL Server, which is the
/// only place the schema itself is exercised: there are no migrations, so the model configuration
/// is what creates this table and nothing else proves it can.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConversationUsageToolCallIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Saving the whole turn as one graph is how the service writes it, which is what lets EF
    /// assign the sequential keys and wire each child's parent — the alternative would be random
    /// identifiers in the clustered key of the largest table in the schema.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_NestedToolCalls_PersistsTheTreeWithParentLinks()
    {
        var modelId = await _fixture.AddModelAsync(
            $"it-toolcall-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(
            modelId: modelId, cancellationToken: TestContext.Current.CancellationToken);
        var (serverId, _) = await _fixture.AddMcpServerAsync(
            $"it-toolcall-mcp-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);

        var usageId = await _fixture.AddConversationUsageWithToolCallsAsync(
            conversationId, modelId, serverId, TestContext.Current.CancellationToken);

        var calls = await _fixture.GetConversationUsageToolCallsAsync(usageId, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls.Count);

        var parent = calls[0];
        var child = calls[1];
        Assert.Equal("ResearchAgent", parent.ToolName);
        Assert.Equal(ConversationToolKinds.Agent, parent.Kind);
        Assert.Null(parent.ParentId);
        Assert.NotEqual(Guid.Empty, parent.Id);

        Assert.Equal("Weather_forecast", child.ToolName);
        Assert.Equal(ConversationToolKinds.McpTool, child.Kind);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(serverId, child.McpServerId);
        Assert.Equal(1, child.Depth);

        // Both rows carry the usage id, including the nested one, so a turn's whole tree loads in
        // one query rather than by walking the parent chain.
        Assert.All(calls, call => Assert.Equal(usageId, call.ConversationUsageId));
    }

    // The invariant the own/subtree column split exists for, asserted against what the database
    // actually stored rather than against the translator's return value.
    [Fact]
    public async Task SaveChangesAsync_NestedToolCalls_KeepsTheSubtreeTotalConsistentWithOwnTokens()
    {
        var modelId = await _fixture.AddModelAsync(
            $"it-toolcall-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(
            modelId: modelId, cancellationToken: TestContext.Current.CancellationToken);

        var usageId = await _fixture.AddConversationUsageWithToolCallsAsync(
            conversationId, modelId, cancellationToken: TestContext.Current.CancellationToken);

        var calls = await _fixture.GetConversationUsageToolCallsAsync(usageId, TestContext.Current.CancellationToken);
        var parent = calls[0];
        var child = calls[1];

        Assert.Equal(parent.SubtreeTotalTokens, parent.TotalTokens + child.SubtreeTotalTokens);

        // Summing the own-token columns over the whole tree counts every token exactly once, which
        // is what makes an aggregate correct under any filter.
        Assert.Equal(100, calls.Sum(c => c.InputTokens ?? 0));
        Assert.Equal(60, calls.Sum(c => c.OutputTokens ?? 0));
    }

    // The audit trail has no cascade behind it by design, and the table references itself, so a
    // set-based delete of the whole table has no ordering guarantee. Pinned here because the
    // per-test reset every other integration class depends on runs through this path.
    [Fact]
    public async Task ResetConversationsAndDocumentsAsync_ToolCallsExist_ClearsThemDespiteTheSelfReference()
    {
        var modelId = await _fixture.AddModelAsync(
            $"it-toolcall-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(
            modelId: modelId, cancellationToken: TestContext.Current.CancellationToken);
        var usageId = await _fixture.AddConversationUsageWithToolCallsAsync(
            conversationId, modelId, cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await _fixture.GetConversationUsageToolCallsAsync(usageId, TestContext.Current.CancellationToken));
    }
}
