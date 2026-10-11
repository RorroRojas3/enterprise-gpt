using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Conversations;

/// <summary>
/// Covers the <c>Core.ConversationUsageTurn</c> table against a real SQL Server. The unique index
/// on it is new schema that nothing else exercises, and both test harnesses build from the model
/// rather than from migrations, so this is the only place it is proven to exist at all.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConversationUsageTurnIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
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
    /// A call reports each iteration once, so a second row for the same one is a defect rather than
    /// a duplicate to tolerate. The index is unfiltered, unlike the other unique indexes in this
    /// model, because these rows are append-only and no soft-deleted row can block a replacement.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_ASecondRowForTheSameIteration_IsRejectedByTheUniqueIndex()
    {
        var (usageId, _) = await ArrangeAsync();

        using var scope = _fixture.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        ctx.ConversationUsageTurns.Add(new ConversationUsageTurn
        {
            ConversationUsageId = usageId,
            Iteration = 0,
            InputTokens = 1,
            OutputTokens = 1,
            TotalTokens = 2,
            DateCreated = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private async Task<(Guid UsageId, Guid ConversationId)> ArrangeAsync()
    {
        var modelId = await _fixture.AddModelAsync(
            $"it-turn-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(
            modelId: modelId, cancellationToken: TestContext.Current.CancellationToken);
        var usageId = await _fixture.AddConversationUsageWithToolCallsAsync(
            conversationId, modelId, cancellationToken: TestContext.Current.CancellationToken);

        return (usageId, conversationId);
    }
}
