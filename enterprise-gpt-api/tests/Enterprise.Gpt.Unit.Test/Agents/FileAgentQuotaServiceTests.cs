using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Covers the per-user ceiling on file generation: that it counts the agent's own audit rows, that it
/// bounds both runs and sandbox seconds, and that an unset ceiling never queries at all.
/// </summary>
public sealed class FileAgentQuotaServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();

    // The agent's own pinned row, which is what marks the audit rows the ceiling counts.
    private readonly Guid _modelId;

    public FileAgentQuotaServiceTests() => _modelId = AddModel();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task CheckAsync_NoCeilingConfigured_Allows()
    {
        await AddAgentRunsAsync(count: 50);

        var decision = await CheckAsync(new FileAgentOptions());

        Assert.True(decision.Allowed);
        Assert.Null(decision.Explanation);
    }

    [Fact]
    public async Task CheckAsync_UnderTheRunCeiling_Allows()
    {
        await AddAgentRunsAsync(count: 2);

        Assert.True((await CheckAsync(new FileAgentOptions { MaxRunsPerUserPerDay = 3 })).Allowed);
    }

    [Fact]
    public async Task CheckAsync_AtTheRunCeiling_RefusesAndSaysWhy()
    {
        await AddAgentRunsAsync(count: 3);

        var decision = await CheckAsync(new FileAgentOptions { MaxRunsPerUserPerDay = 3 });

        Assert.False(decision.Allowed);
        Assert.Contains("3", decision.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_AtTheSandboxSecondCeiling_Refuses()
    {
        await AddAgentRunsAsync(count: 2, durationMs: 30_000);

        var decision = await CheckAsync(new FileAgentOptions { MaxSandboxSecondsPerUserPerDay = 60 });

        Assert.False(decision.Allowed);
        Assert.Contains("seconds", decision.Explanation!, StringComparison.Ordinal);
    }

    // Ten fast runs and one very slow one cost differently, which is why a run count alone is not the
    // whole ceiling.
    [Fact]
    public async Task CheckAsync_UnderTheRunCeilingButOverTheSecondsOne_Refuses()
    {
        await AddAgentRunsAsync(count: 1, durationMs: 600_000);

        var decision = await CheckAsync(
            new FileAgentOptions { MaxRunsPerUserPerDay = 100, MaxSandboxSecondsPerUserPerDay = 60 });

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task CheckAsync_RunsOlderThanADay_DoesNotCountThem()
    {
        await AddAgentRunsAsync(count: 5, ageHours: 25);

        Assert.True((await CheckAsync(new FileAgentOptions { MaxRunsPerUserPerDay = 3 })).Allowed);
    }

    [Fact]
    public async Task CheckAsync_AnotherUsersRuns_DoesNotCountThem()
    {
        await AddAgentRunsAsync(count: 5);

        var decision = await CheckAsync(
            new FileAgentOptions { MaxRunsPerUserPerDay = 3 }, caller: Guid.NewGuid());

        Assert.True(decision.Allowed);
    }

    // Only the agent's own root rows. Its nested calls share the turn and would count the same run
    // several times over.
    [Fact]
    public async Task CheckAsync_CallsNestedUnderTheAgent_DoesNotCountThem()
    {
        await AddAgentRunsAsync(count: 1, depth: 1);

        Assert.True((await CheckAsync(new FileAgentOptions { MaxRunsPerUserPerDay = 1 })).Allowed);
    }

    [Fact]
    public async Task CheckAsync_RowsForAnotherModel_DoesNotCountThem()
    {
        await AddAgentRunsAsync(count: 5, modelId: AddModel());

        Assert.True((await CheckAsync(new FileAgentOptions { MaxRunsPerUserPerDay = 3 })).Allowed);
    }

    private Task<FileAgentQuotaDecision> CheckAsync(FileAgentOptions options, Guid? caller = null) =>
        new FileAgentQuotaService(_fixture.Context, Options.Create(options))
            .CheckAsync(caller ?? KnownIds.SeedUserId, _modelId, TestContext.Current.CancellationToken);

    private async Task AddAgentRunsAsync(
        int count,
        long durationMs = 1_000,
        int ageHours = 0,
        int depth = 0,
        Guid? modelId = null)
    {
        var date = DateTimeOffset.UtcNow.AddHours(-ageHours);

        using var ctx = _fixture.CreateContext();

        var usage = new ConversationUsage
        {
            Id = Guid.NewGuid(),
            ConversationId = await AddConversationAsync(ctx, KnownIds.SeedUserId, date),
            UserId = KnownIds.SeedUserId,
            ModelId = KnownIds.SeedModelId,
            ProviderId = KnownIds.SeedProviderId,
            DeploymentName = "deployment",
            Kind = ConversationUsageKinds.Chat,
            Status = ConversationUsageStatuses.Completed,
            DateCreated = date
        };

        for (var index = 0; index < count; index++)
        {
            usage.ToolCalls.Add(new ConversationUsageToolCall
            {
                Id = Guid.NewGuid(),
                Sequence = index,
                Depth = depth,
                Kind = ConversationToolKinds.Agent,
                ToolName = FileAgentToolNames.Agent,
                ModelId = modelId ?? _modelId,
                DeploymentName = "file-agent",
                DurationMs = durationMs,
                Succeeded = true,
                DateCreated = date
            });
        }

        ctx.ConversationUsage.Add(usage);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Guid AddModel()
    {
        var date = DateTimeOffset.UtcNow;
        var model = new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = KnownIds.SeedProviderId,
            Name = $"Agent model {Guid.NewGuid():N}",
            DeploymentName = "file-agent",
            Description = "Pinned to the File Agent.",
            ContextWindowSize = 1000m,
            MaxOutputTokens = 100m,
            IsToolEnabled = true,
            IsUserSelectable = false,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        };

        using var ctx = _fixture.CreateContext();
        ctx.Models.Add(model);
        ctx.SaveChanges();

        return model.Id;
    }

    private static async Task<Guid> AddConversationAsync(
        Repository.EnterpriseGptDbContext ctx, Guid userId, DateTimeOffset date)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date
        };

        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation.Id;
    }
}
