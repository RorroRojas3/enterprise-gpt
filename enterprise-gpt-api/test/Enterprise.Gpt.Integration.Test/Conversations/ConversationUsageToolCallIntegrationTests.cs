using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Conversations;

/// <summary>
/// Covers <c>Core.ConversationUsageToolCall</c> on real SQL Server, the only place its schema is exercised.
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
    /// The service saves a turn as one graph so EF assigns sequential keys and wires each child's parent.
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

    // The audit trail has no cascade and the table references itself, so a set-based delete has no
    // ordering guarantee; every integration class's per-test reset runs through this path.
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
