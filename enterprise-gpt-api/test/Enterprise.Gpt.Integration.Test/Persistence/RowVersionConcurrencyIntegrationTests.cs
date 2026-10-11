using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Persistence;

/// <summary>
/// The row version is only a concurrency token on the real engine, so the race the services translate into a
/// 409 is proven here rather than on SQLite.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RowVersionConcurrencyIntegrationTests(IntegrationTestFixture fixture)
{
    private readonly IntegrationTestFixture _fixture = fixture;

    [Fact]
    public async Task SaveChanges_RowChangedSinceItWasRead_ThrowsConcurrencyException()
    {
        var projectId = await _fixture.AddProjectAsync(
            name: $"row-version-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);

        using var first = _fixture.CreateContext();
        using var second = _fixture.CreateContext();
        var winner = await first.Projects.SingleAsync(x => x.Id == projectId, TestContext.Current.CancellationToken);
        var loser = await second.Projects.SingleAsync(x => x.Id == projectId, TestContext.Current.CancellationToken);

        winner.Description = "first";
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        loser.Description = "second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
