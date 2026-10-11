using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Repository.Sql.Interceptors;

public sealed class SoftDeleteInterceptorTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(_start);
    private readonly SqliteDbContextFixture _fixture;

    public SoftDeleteInterceptorTests()
    {
        _fixture = new SqliteDbContextFixture(_time);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SavingChanges_RemovedEntity_KeepsTheRowStampedDeactivatedAndModified()
    {
        var project = await AddProjectAsync();
        _time.Advance(TimeSpan.FromHours(1));

        _fixture.Context.Remove(project);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Projects.IncludingDeactivated().AsNoTracking()
            .SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(_start.AddHours(1), stored.DateDeactivated);
        Assert.Equal(_start.AddHours(1), stored.DateModified);
        Assert.Equal(_start, stored.DateCreated);
    }

    [Fact]
    public async Task SavingChanges_RemovedEntity_IsHiddenFromFilteredQueries()
    {
        var project = await AddProjectAsync();

        _fixture.Context.Remove(project);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        Assert.False(await ctx.Projects.AnyAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken));
    }

    #region Private methods

    private async Task<Project> AddProjectAsync()
    {
        var project = new Project { Id = Guid.NewGuid(), UserId = KnownIds.SeedUserId, Name = $"aaa-{Guid.NewGuid():N}" };
        _fixture.Context.Add(project);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return project;
    }

    #endregion
}
