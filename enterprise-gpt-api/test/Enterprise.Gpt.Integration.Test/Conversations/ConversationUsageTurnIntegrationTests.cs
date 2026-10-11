using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Conversations;

/// <summary>
/// Covers <c>Core.ConversationUsageTurn</c> on real SQL Server, the only place its unique index is proven.
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
    /// Each iteration is reported once; the index is unfiltered because these rows are append-only.
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
