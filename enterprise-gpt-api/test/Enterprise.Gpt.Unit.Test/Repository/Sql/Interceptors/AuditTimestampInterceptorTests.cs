using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Repository.Sql.Interceptors;

public sealed class AuditTimestampInterceptorTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(_start);
    private readonly SqliteDbContextFixture _fixture;

    public AuditTimestampInterceptorTests()
    {
        _fixture = new SqliteDbContextFixture(_time);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SavingChanges_AddedModifiedEntity_StampsCreatedAndModifiedWithTheSameInstant()
    {
        var project = NewProject();

        _fixture.Context.Add(project);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await ReadProjectAsync(project.Id);
        Assert.Equal(_start, stored.DateCreated);
        Assert.Equal(_start, stored.DateModified);
    }

    [Fact]
    public async Task SavingChanges_ModifiedEntity_StampsModifiedAndLeavesCreated()
    {
        var project = NewProject();
        _fixture.Context.Add(project);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(5));
        project.Name = "aaa-renamed";
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await ReadProjectAsync(project.Id);
        Assert.Equal(_start, stored.DateCreated);
        Assert.Equal(_start.AddMinutes(5), stored.DateModified);
    }

    [Fact]
    public async Task SavingChanges_StampSetByTheCaller_IsOverwritten()
    {
        var project = NewProject();
        project.DateCreated = _start.AddYears(-1);

        _fixture.Context.Add(project);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_start, (await ReadProjectAsync(project.Id)).DateCreated);
    }

    [Fact]
    public async Task SavingChanges_AddedCreatedOnlyEntity_StampsCreated()
    {
        var provider = new Provider { Id = Guid.NewGuid(), Name = "aaa-provider" };

        _fixture.Context.Add(provider);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Providers.SingleAsync(x => x.Id == provider.Id, TestContext.Current.CancellationToken);
        Assert.Equal(_start, stored.DateCreated);
    }

    [Fact]
    public void SavingChanges_SynchronousSave_StampsLikeTheAsynchronousOne()
    {
        var project = NewProject();

        _fixture.Context.Add(project);
        _fixture.Context.SaveChanges();

        using var ctx = _fixture.CreateContext();
        var stored = ctx.Projects.IncludingDeactivated().AsNoTracking().Single(x => x.Id == project.Id);
        Assert.Equal(_start, stored.DateCreated);
        Assert.Equal(_start, stored.DateModified);
    }

    /// <summary>
    /// One clock reading per save, so a usage row and every child row it brings share one stamp.
    /// </summary>
    [Fact]
    public async Task SavingChanges_UsageGraph_SharesOneStampAcrossEveryRow()
    {
        _time.AutoAdvanceAmount = TimeSpan.FromMilliseconds(1);
        var conversation = new Conversation { Id = Guid.NewGuid(), UserId = KnownIds.SeedUserId, Name = "aaa-graph" };
        _fixture.Context.Add(conversation);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var usage = new ConversationUsage
        {
            ConversationId = conversation.Id,
            UserId = KnownIds.SeedUserId,
            ModelId = KnownIds.SeedModelId,
            ProviderId = KnownIds.SeedProviderId,
            DeploymentName = "aaa-deployment",
            Kind = ConversationUsageKinds.Chat,
            Status = ConversationUsageStatuses.Completed,
            Turns = [new ConversationUsageTurn { Iteration = 0 }, new ConversationUsageTurn { Iteration = 1 }]
        };
        _fixture.Context.Add(usage);
        await _fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var turns = await ctx.ConversationUsageTurns.IncludingDeactivated().AsNoTracking()
            .Where(x => x.ConversationUsageId == usage.Id)
            .Select(x => x.DateCreated)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, turns.Count);
        Assert.All(turns, stamp => Assert.Equal(usage.DateCreated, stamp));
    }

    #region Private methods

    private static Project NewProject() => new()
    {
        Id = Guid.NewGuid(),
        UserId = KnownIds.SeedUserId,
        Name = $"aaa-{Guid.NewGuid():N}"
    };

    private async Task<Project> ReadProjectAsync(Guid id)
    {
        using var ctx = _fixture.CreateContext();
        return await ctx.Projects.IncludingDeactivated().AsNoTracking()
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    #endregion
}
