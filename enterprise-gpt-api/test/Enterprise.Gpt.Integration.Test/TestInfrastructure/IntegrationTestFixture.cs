using System.Text.Json;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Entity.Transcripts.Models;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Entity.Users;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Permissions;
using Enterprise.Gpt.Service.Prompts;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Owns the SQL Server 2025 Testcontainer (older engines reject <c>UseCompatibilityLevel(170)</c>) and the
/// shared <see cref="CustomWebApplicationFactory"/>; creates the schema and seeds <see cref="TestUsers"/>.
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest")
            .Build();

    // Seeded by PermissionConfiguration. Resets must keep them: the schema is created once per run.
    private static readonly Guid[] _builtInPermissionIds =
        [PermissionIds.Administrator, PermissionIds.UploadFile, PermissionIds.GenerateFiles];

    public CustomWebApplicationFactory Factory { get; private set; } = null!;

    private DbContextOptions<EnterpriseGptDbContext> _bareOptions = null!;

    public string ConnectionString { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EnterpriseGptTests"
        }.ConnectionString;

        ConnectionString = connectionString;
        Factory = new CustomWebApplicationFactory(connectionString);
        _bareOptions = new DbContextOptionsBuilder<EnterpriseGptDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
            .Options;

        using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        await SeedTestUsersAsync(ctx);
    }

    /// <summary>
    /// A context without the production interceptors, so arranged audit stamps are stored as stated.
    /// </summary>
    public EnterpriseGptDbContext CreateContext() => new(_bareOptions);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// Restores the model catalog to its seed baseline. Mutating tests must use <see cref="AddModelAsync"/>
    /// rows: this does not restore the seeded model's name or limits.
    /// </summary>
    public async Task ResetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        await ClearConversationUsageAsync(ctx, cancellationToken);

        // Required FK to the model, like the usage rows; any future required FK to Core.Ref.Model
        // belongs here too.
        await ClearDocumentSummariesAsync(ctx, cancellationToken);

        // Release (not delete) the nullable Conversation.ModelId before the model delete; the rows can
        // belong to an earlier test class, which made the FK failure order-dependent.
        await ctx.Conversations.IncludingDeactivated()
            .Where(x => x.ModelId != null && x.ModelId != KnownIds.SeedModelId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ModelId, (Guid?)null), cancellationToken);

        await ctx.Models
            .Where(x => x.Id != KnownIds.SeedModelId)
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Models
            .Where(x => x.Id == KnownIds.SeedModelId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null)
                .SetProperty(p => p.IsDefault, false)
                // Its seeded state, so a class that made it selectable cannot leak into the next.
                .SetProperty(p => p.IsUserSelectable, false), cancellationToken);
    }

    public async Task<Guid> AddModelAsync(
        string name, bool isDefault = false, bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var model = new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = KnownIds.SeedProviderId,
            Name = name,
            DeploymentName = $"{name}-deployment",
            Description = $"{name} description",
            ContextWindowSize = 1000m,
            MaxOutputTokens = 100m,
            IsToolEnabled = true,
            IsDefault = isDefault,
            DateDeactivated = deactivated ? date : null,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        };

        ctx.Models.Add(model);
        await ctx.SaveChangesAsync(cancellationToken);

        return model.Id;
    }

    public async Task<Model?> FindModelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task ResetMcpServersAndPermissionsAsync(CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        await ctx.UserPermissions.IncludingDeactivated()
            .Where(x => x.UserId != TestUsers.AdminUserId || x.PermissionId != PermissionIds.Administrator)
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions.IncludingDeactivated()
            .Where(x => !_builtInPermissionIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions.IncludingDeactivated()
            .Where(x => _builtInPermissionIds.Contains(x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null), cancellationToken);
        await ClearConversationUsageAsync(ctx, cancellationToken);
        // Before the servers: the FK is NoAction, so a leftover credential would violate it.
        await ctx.UserMcpCredentials.IncludingDeactivated()
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.McpServers
            .ExecuteDeleteAsync(cancellationToken);

        ClearPermissionCache();
    }

    public async Task<(Guid ServerId, Guid? PermissionId)> AddMcpServerAsync(
        string name, int authType = 1, string? scope = null, bool deactivated = false,
        bool withPermission = true, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            Url = "https://mcp.example.com/sse",
            AuthType = (McpAuthTypes)authType,
            Scope = scope,
            DateDeactivated = deactivated ? date : null,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        };
        ctx.McpServers.Add(server);

        Guid? permissionId = null;
        if (withPermission)
        {
            var permission = new Permission
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = $"Access to the {name} MCP server.",
                McpServerId = server.Id,
                DateDeactivated = deactivated ? date : null,
                DateCreated = date,
                DateModified = date,
                CreatedById = TestUsers.AdminUserId,
                ModifiedById = TestUsers.AdminUserId
            };
            ctx.Permissions.Add(permission);
            permissionId = permission.Id;
        }

        await ctx.SaveChangesAsync(cancellationToken);

        return (server.Id, permissionId);
    }

    public async Task<Guid> AddUserPermissionAsync(
        Guid userId, Guid permissionId, bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var grant = new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionId = permissionId,
            DateDeactivated = deactivated ? date : null,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        };
        ctx.UserPermissions.Add(grant);
        await ctx.SaveChangesAsync(cancellationToken);

        ClearPermissionCache();

        return grant.Id;
    }

    public async Task<McpServer?> FindMcpServerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.McpServers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<UserMcpCredential?> FindUserMcpCredentialAsync(
        Guid userId, Guid mcpServerId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.UserMcpCredentials.IncludingDeactivated()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.McpServerId == mcpServerId, cancellationToken);
    }

    public async Task<Permission?> FindPermissionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.Permissions.IncludingDeactivated()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<Permission?> FindPermissionByMcpServerAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.Permissions.IncludingDeactivated()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.McpServerId == mcpServerId, cancellationToken);
    }

    public async Task<List<UserPermission>> FindUserPermissionsAsync(
        Guid userId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.UserPermissions.IncludingDeactivated()
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.PermissionId == permissionId)
            .ToListAsync(cancellationToken);
    }

    public async Task ResetUsersAsync(CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        await ctx.UserPermissions.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions.IncludingDeactivated()
            .Where(x => !_builtInPermissionIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions.IncludingDeactivated()
            .Where(x => _builtInPermissionIds.Contains(x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null), cancellationToken);
        // The seeded system user owns the Administrator permission's audit columns, so it can
        // never be deleted; the two test identities are restored rather than recreated.
        await ctx.Users
            .Where(x => x.Id != KnownIds.SeedUserId
                && x.Id != TestUsers.AdminUserId
                && x.Id != TestUsers.RegularUserId)
            .ExecuteDeleteAsync(cancellationToken);

        var date = DateTimeOffset.UtcNow;
        await ctx.Users
            .Where(x => x.Id == TestUsers.AdminUserId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.FirstName, "Admin")
                .SetProperty(p => p.LastName, "Tester")
                .SetProperty(p => p.Email, "admin.tester@example.com")
                .SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null), cancellationToken);
        await ctx.Users
            .Where(x => x.Id == TestUsers.RegularUserId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.FirstName, "Regular")
                .SetProperty(p => p.LastName, "Tester")
                .SetProperty(p => p.Email, "regular.tester@example.com")
                .SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null), cancellationToken);

        ctx.UserPermissions.Add(new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = TestUsers.AdminUserId,
            PermissionId = PermissionIds.Administrator,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        });
        await ctx.SaveChangesAsync(cancellationToken);

        Factory.GraphService.Reset();
        ClearPermissionCache();

    }

    public async Task<Guid> AddPermissionAsync(
        string name, bool isDefault = false, bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            IsDefault = isDefault,
            DateDeactivated = deactivated ? date : null,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        };

        ctx.Permissions.Add(permission);
        await ctx.SaveChangesAsync(cancellationToken);

        return permission.Id;
    }

    public async Task<Guid> AddUserAsync(
        string firstName, string lastName, string email, bool deactivated = false,
        CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            DateDeactivated = deactivated ? date : null,
            DateCreated = date,
            DateModified = date
        };

        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(cancellationToken);

        return user.Id;
    }

    public async Task<User?> FindUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.Users
            .IncludingDeactivated()
            .AsNoTracking()
            .Include(u => u.UserPermissions)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task ResetConversationsAndDocumentsAsync(CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        // Before their documents: like the chunks, these FKs have no cascade.
        await ClearDocumentSummariesAsync(ctx, cancellationToken);
        await ClearDocumentSheetsAsync(ctx, cancellationToken);

        await ctx.ProjectDocumentChunks.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ProjectDocuments.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocumentChunks.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocuments.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        // Before the conversations: like the usage rows, the feedback FK has no cascade.
        await ctx.MessageFeedback.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ClearConversationUsageAsync(ctx, cancellationToken);
        await ctx.Conversations.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.Projects.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);

        // Other classes' resets clear grants wholesale, so the upload grants are restored, not assumed.
        await EnsureUploadFileGrantsAsync(ctx, cancellationToken);

        Factory.BlobStorage.Reset();
        Factory.EmbeddingGenerator.Reset();
        Factory.Transcripts.Reset();
        ClearPermissionCache();

    }

    /// <summary>
    /// Called by every reset that deletes documents or models: both FKs are required, with no cascade.
    /// </summary>
    private static async Task ClearDocumentSummariesAsync(
        EnterpriseGptDbContext ctx, CancellationToken cancellationToken)
    {
        await ctx.ProjectDocumentSummaries.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocumentSummaries.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Leaf first; every reset deleting documents calls this, as the chain has no cascade.</summary>
    private static async Task ClearDocumentSheetsAsync(
        EnterpriseGptDbContext ctx, CancellationToken cancellationToken)
    {
        await ctx.ProjectDocumentSheetRows.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ProjectDocumentSheetColumns.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ProjectDocumentSheets.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocumentSheetRows.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocumentSheetColumns.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocumentSheets.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Children first. The audit trail deliberately has no cascade, so every reset that deletes a table
    /// it references calls this.
    /// </summary>
    private static async Task ClearConversationUsageAsync(EnterpriseGptDbContext ctx, CancellationToken cancellationToken)
    {
        await ctx.ConversationUsageMcpServers.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);

        // Tool calls nest through a self-referencing key, and a set-based delete gives no ordering
        // guarantee over one, so the parent links are broken before the rows go.
        await ctx.ConversationUsageToolCalls.IncludingDeactivated()
            .Where(x => x.ParentId != null)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ParentId, (Guid?)null), cancellationToken);
        await ctx.ConversationUsageToolCalls.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);

        await ctx.ConversationUsageTurns.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);

        await ctx.ConversationUsage.IncludingDeactivated().ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Prices default to <see langword="null"/>, which the report counts as unpriced rather than free.
    /// Cancelled and failed turns are never transcribed, so their <paramref name="contextTokens"/> is null.
    /// </summary>
    public async Task<Guid> AddConversationUsageAsync(
        Guid conversationId, Guid modelId, DateTimeOffset dateCreated, Guid? userId = null,
        IReadOnlyCollection<Guid>? mcpServerIds = null, ConversationUsageKinds kind = ConversationUsageKinds.Chat,
        ConversationUsageStatuses status = ConversationUsageStatuses.Completed,
        long inputTokens = 10, long outputTokens = 5,
        long toolInputTokens = 0, long toolOutputTokens = 0,
        long? contextTokens = null,
        decimal? inputPricePerMillionTokens = null, decimal? outputPricePerMillionTokens = null,
        CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var usage = new ConversationUsage
        {
            ConversationId = conversationId,
            UserId = userId ?? TestUsers.RegularUserId,
            ModelId = modelId,
            ProviderId = KnownIds.SeedProviderId,
            DeploymentName = "integration-deployment",
            Kind = kind,
            Status = status,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ToolInputTokens = toolInputTokens,
            ToolOutputTokens = toolOutputTokens,
            ContextTokens = contextTokens,
            InputPricePerMillionTokens = inputPricePerMillionTokens,
            OutputPricePerMillionTokens = outputPricePerMillionTokens,
            DateCreated = dateCreated,
            McpServers =
            [
                .. (mcpServerIds ?? []).Select(id => new ConversationUsageMcpServer
                {
                    McpServerId = id,
                    McpServerName = $"Server {id:N}",
                    DateCreated = dateCreated
                })
            ]
        };

        ctx.ConversationUsage.Add(usage);
        await ctx.SaveChangesAsync(cancellationToken);

        return usage.Id;
    }

    /// <summary>
    /// Inserts a nested tool-call tree as one graph, the way <c>ConversationService</c> writes it, so the
    /// self-referencing key and sequential key generation are exercised.
    /// </summary>
    public async Task<Guid> AddConversationUsageWithToolCallsAsync(
        Guid conversationId, Guid modelId, Guid? mcpServerId = null, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var child = new ConversationUsageToolCall
        {
            Sequence = 0,
            Depth = 1,
            Kind = ConversationToolKinds.McpTool,
            ToolName = "Weather_forecast",
            Source = "Weather",
            McpServerId = mcpServerId,
            // The enclosing root's iteration, not one of its own: that is what the middleware
            // reports for a nested call, and what makes a delta query group correctly.
            Iteration = 1,
            InputTokens = 30,
            OutputTokens = 20,
            TotalTokens = 50,
            SubtreeTotalTokens = 50,
            DurationMs = 400,
            Succeeded = true,
            DateCreated = date
        };
        var parent = new ConversationUsageToolCall
        {
            Sequence = 0,
            Depth = 0,
            Kind = ConversationToolKinds.Agent,
            ToolName = "ResearchAgent",
            Source = "Research Agent",
            CallId = "call_1",
            Iteration = 1,
            InputTokens = 70,
            OutputTokens = 40,
            TotalTokens = 110,
            SubtreeTotalTokens = 160,
            DurationMs = 1500,
            Succeeded = true,
            DateCreated = date,
            Children = [child]
        };

        var usage = new ConversationUsage
        {
            ConversationId = conversationId,
            UserId = TestUsers.RegularUserId,
            ModelId = modelId,
            ProviderId = KnownIds.SeedProviderId,
            DeploymentName = "integration-deployment",
            Kind = ConversationUsageKinds.Chat,
            Status = ConversationUsageStatuses.Completed,
            InputTokens = 11,
            OutputTokens = 7,
            ToolInputTokens = 100,
            ToolOutputTokens = 60,
            DateCreated = date,
            // Flat, nested row included: this navigation is what gives each row its owning usage
            // id, and a child reachable only through its parent would be saved without one.
            ToolCalls = [parent, child],
            // Two model turns whose inputs and outputs partition the assistant columns above, so a
            // reader can check that invariant against a real database rather than only in memory.
            Turns =
            [
                new ConversationUsageTurn
                {
                    Iteration = 0,
                    ResponseId = "resp-0",
                    InputTokens = 4,
                    OutputTokens = 3,
                    TotalTokens = 7,
                    DateCreated = date
                },
                new ConversationUsageTurn
                {
                    Iteration = 1,
                    ResponseId = "resp-1",
                    InputTokens = 7,
                    OutputTokens = 4,
                    TotalTokens = 11,
                    DateCreated = date
                }
            ]
        };

        ctx.ConversationUsage.Add(usage);
        await ctx.SaveChangesAsync(cancellationToken);

        return usage.Id;
    }

    public async Task<List<ConversationUsageToolCall>> GetConversationUsageToolCallsAsync(
        Guid usageId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.ConversationUsageToolCalls.IncludingDeactivated()
            .AsNoTracking()
            .Where(x => x.ConversationUsageId == usageId)
            .OrderBy(x => x.Depth)
            .ThenBy(x => x.Sequence)
            .ToListAsync(cancellationToken);
    }

    private static async Task EnsureUploadFileGrantsAsync(EnterpriseGptDbContext ctx, CancellationToken cancellationToken)
    {
        Guid[] userIds = [TestUsers.AdminUserId, TestUsers.RegularUserId];

        var granted = await ctx.UserPermissions.IncludingDeactivated()
            .AsNoTracking()
            .Where(x => x.PermissionId == PermissionIds.UploadFile && userIds.Contains(x.UserId))
            .Select(x => x.UserId)
            .ToListAsync(cancellationToken);

        var date = DateTimeOffset.UtcNow;
        foreach (var userId in userIds.Except(granted))
        {
            ctx.UserPermissions.Add(new UserPermission
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PermissionId = PermissionIds.UploadFile,
                DateCreated = date,
                DateModified = date,
                CreatedById = TestUsers.AdminUserId,
                ModifiedById = TestUsers.AdminUserId
            });
        }

        await ctx.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> AddConversationAsync(
        Guid? userId = null, bool deactivated = false, Guid? projectId = null, Guid? modelId = null,
        string name = "Integration Conversation", CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? TestUsers.RegularUserId,
            ProjectId = projectId,
            ModelId = modelId,
            Name = name,
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        };

        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(cancellationToken);

        return conversation.Id;
    }

    /// <summary>
    /// <paramref name="rating"/> is seeded because <see cref="FakeTranscriptStore"/> ignores patches, so a
    /// rating set through the API is invisible to a later withdrawal.
    /// </summary>
    public async Task<Guid> AddTranscriptMessageAsync(
        Guid conversationId, ChatRoles role = ChatRoles.Assistant, Guid? userId = null,
        MessageFeedbackRatings? rating = null, CancellationToken cancellationToken = default)
    {
        var owner = userId ?? TestUsers.RegularUserId;
        var messageId = Guid.NewGuid();

        var message = TranscriptMessageDocument.Create(
            owner, conversationId, messageId, role, "Seeded message.", DateTimeOffset.UtcNow) with
        {
            Feedback = rating is { } verdict
                ? new TranscriptMessageFeedback { Rating = verdict, DateModified = DateTimeOffset.UtcNow }
                : null
        };

        // Through the batch surface, the only production write path, so it is keyed as a real one is.
        await Factory.Transcripts.ExecuteBatchAsync(
            new Microsoft.Azure.Cosmos.PartitionKey(PartitionKeys.For(owner, conversationId)),
            batch => batch.Create(message.Id, message),
            cancellationToken);

        return messageId;
    }

    public async Task<MessageFeedback?> ReadMessageFeedbackAsync(
        Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.MessageFeedback.IncludingDeactivated()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ConversationId == conversationId && x.MessageId == messageId, cancellationToken);
    }

    /// <summary><paramref name="name"/> must be unique per user among active projects.</summary>
    public async Task<Guid> AddProjectAsync(
        string name = "Integration Project", Guid? userId = null, string? instructions = null,
        bool deactivated = false, bool isFavorite = false, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? TestUsers.RegularUserId,
            Name = name,
            Description = $"{name} description",
            Instructions = instructions,
            IsFavorite = isFavorite,
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        };

        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync(cancellationToken);

        return project.Id;
    }

    /// <summary>
    /// Timestamps descend one second per name, so the newest-first listing keeps the given order; a
    /// shared instant would order by id.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AddConversationDocumentsAsync(
        Guid conversationId, IReadOnlyList<string> names, Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var now = DateTimeOffset.UtcNow;
        var documents = names
            .Select((name, index) =>
            {
                var date = now.AddSeconds(-index);

                return new ConversationDocument
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    UserId = userId ?? TestUsers.RegularUserId,
                    Name = name,
                    Extension = Path.GetExtension(name),
                    MimeType = "application/octet-stream",
                    Size = 1024,
                    Path = $"integration/{name}",
                    DateCreated = date,
                    DateModified = date
                };
            })
            .ToList();

        ctx.ConversationDocuments.AddRange(documents);
        await ctx.SaveChangesAsync(cancellationToken);

        return [.. documents.Select(x => x.Id)];
    }

    /// <summary>
    /// Writes hand-chosen chunk vectors rather than ingesting, so retrieval distances are predictable
    /// instead of depending on a fixture's prose.
    /// </summary>
    public async Task<Guid> AddConversationDocumentAsync(
        Guid conversationId, string name, IReadOnlyList<SeedChunk> chunks, bool deactivated = false,
        bool chunksDeactivated = false, Guid? userId = null,
        ConversationDocumentTypes type = ConversationDocumentTypes.Uploaded,
        CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var document = new ConversationDocument
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId ?? TestUsers.RegularUserId,
            Type = type,
            Name = name,
            Extension = Path.GetExtension(name),
            MimeType = "application/octet-stream",
            Size = 1024,
            Path = $"integration/{name}",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null,
            Chunks =
            [
                .. chunks.Select(c => new ConversationDocumentChunk
                {
                    Id = Guid.NewGuid(),
                    Index = c.Index,
                    SourceNumber = c.SourceNumber,
                    TokenCount = Math.Max(1, c.Text.Length / 4),
                    Text = c.Text,
                    Embedding = new SqlVector<float>(c.Vector),
                    DateCreated = date,
                    DateModified = date,
                    DateDeactivated = deactivated || chunksDeactivated ? date : null
                })
            ]
        };

        ctx.ConversationDocuments.Add(document);
        await ctx.SaveChangesAsync(cancellationToken);

        return document.Id;
    }

    public async Task AddConversationDocumentSheetAsync(
        Guid documentId, int sheetIndex, string sheetName, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;

        ctx.ConversationDocumentSheets.Add(new ConversationDocumentSheet
        {
            Id = Guid.NewGuid(),
            ConversationDocumentId = documentId,
            SheetIndex = sheetIndex,
            SheetName = sheetName,
            RowCount = 1,
            ColumnCount = 1,
            DateCreated = date,
            DateModified = date,
            Columns =
            [
                new ConversationDocumentSheetColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnIndex = 0,
                    ColumnName = "SKU",
                    InferredType = SheetColumnTypes.Text,
                    DateCreated = date,
                    DateModified = date
                }
            ],
            Rows =
            [
                new ConversationDocumentSheetRow
                {
                    Id = Guid.NewGuid(),
                    RowIndex = 0,
                    Cells = """{"SKU":"W-1"}""",
                    DateCreated = date,
                    DateModified = date
                }
            ]
        });

        await ctx.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> AddConversationDocumentSheetAsync(
        Guid documentId,
        SeedSheet sheet,
        bool rowsDeactivated = false,
        CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;

        var row = new ConversationDocumentSheet
        {
            Id = Guid.NewGuid(),
            ConversationDocumentId = documentId,
            SheetIndex = sheet.SheetIndex,
            SheetName = sheet.Name,
            RowCount = sheet.Rows.Count,
            ColumnCount = sheet.Columns.Count,
            DateCreated = date,
            DateModified = date,
            Columns =
            [
                .. sheet.Columns.Select((c, i) => new ConversationDocumentSheetColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnIndex = i,
                    ColumnName = c.Name,
                    InferredType = c.Type,
                    DateCreated = date,
                    DateModified = date
                })
            ],
            Rows =
            [
                .. sheet.Rows.Select((cells, i) => new ConversationDocumentSheetRow
                {
                    Id = Guid.NewGuid(),
                    RowIndex = i,
                    Cells = SeedSheetCells(cells),
                    DateCreated = date,
                    DateModified = date,
                    DateDeactivated = rowsDeactivated ? date : null
                })
            ]
        };

        ctx.ConversationDocumentSheets.Add(row);
        await ctx.SaveChangesAsync(cancellationToken);

        return row.Id;
    }

    public async Task<Guid> AddProjectDocumentSheetAsync(
        Guid documentId, SeedSheet sheet, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;

        var row = new ProjectDocumentSheet
        {
            Id = Guid.NewGuid(),
            ProjectDocumentId = documentId,
            SheetIndex = sheet.SheetIndex,
            SheetName = sheet.Name,
            RowCount = sheet.Rows.Count,
            ColumnCount = sheet.Columns.Count,
            DateCreated = date,
            DateModified = date,
            Columns =
            [
                .. sheet.Columns.Select((c, i) => new ProjectDocumentSheetColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnIndex = i,
                    ColumnName = c.Name,
                    InferredType = c.Type,
                    DateCreated = date,
                    DateModified = date
                })
            ],
            Rows =
            [
                .. sheet.Rows.Select((cells, i) => new ProjectDocumentSheetRow
                {
                    Id = Guid.NewGuid(),
                    RowIndex = i,
                    Cells = SeedSheetCells(cells),
                    DateCreated = date,
                    DateModified = date
                })
            ]
        };

        ctx.ProjectDocumentSheets.Add(row);
        await ctx.SaveChangesAsync(cancellationToken);

        return row.Id;
    }

    /// <summary>Mirrors ingestion: one string property per populated cell, none for an empty one.</summary>
    private static string SeedSheetCells(IReadOnlyDictionary<string, string> cells) =>
        JsonSerializer.Serialize(cells.Where(c => c.Value.Length > 0).ToDictionary(c => c.Key, c => c.Value));

    public async Task<Guid> AddProjectDocumentAsync(
        Guid projectId, string name, IReadOnlyList<SeedChunk> chunks, Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;
        var document = new ProjectDocument
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            UserId = userId ?? TestUsers.RegularUserId,
            Name = name,
            Extension = Path.GetExtension(name),
            MimeType = "application/octet-stream",
            Size = 1024,
            Path = $"integration/projects/{name}",
            DateCreated = date,
            DateModified = date,
            Chunks =
            [
                .. chunks.Select(c => new ProjectDocumentChunk
                {
                    Id = Guid.NewGuid(),
                    Index = c.Index,
                    SourceNumber = c.SourceNumber,
                    TokenCount = Math.Max(1, c.Text.Length / 4),
                    Text = c.Text,
                    Embedding = new SqlVector<float>(c.Vector),
                    DateCreated = date,
                    DateModified = date
                })
            ]
        };

        ctx.ProjectDocuments.Add(document);
        await ctx.SaveChangesAsync(cancellationToken);

        return document.Id;
    }

    public async Task<Project?> FindProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.Projects.IncludingDeactivated()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<Conversation?> FindConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.Conversations.IncludingDeactivated()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<ProjectDocument?> FindProjectDocumentAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.ProjectDocuments.IncludingDeactivated()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == documentId, cancellationToken);
    }

    public async Task<List<ProjectDocumentChunk>> FindProjectDocumentChunksAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.ProjectDocumentChunks.IncludingDeactivated()
            .AsNoTracking()
            .Where(x => x.ProjectDocumentId == documentId)
            .OrderBy(x => x.Index)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ConversationDocumentChunk>> FindDocumentChunksAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.ConversationDocumentChunks.IncludingDeactivated()
            .AsNoTracking()
            .Where(x => x.ConversationDocumentId == documentId)
            .OrderBy(x => x.Index)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ConversationDocumentSheet>> FindDocumentSheetsAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.ConversationDocumentSheets.IncludingDeactivated()
            .AsNoTracking()
            .Include(x => x.Columns)
            .Include(x => x.Rows)
            .Where(x => x.ConversationDocumentId == documentId)
            .OrderBy(x => x.SheetIndex)
            .ToListAsync(cancellationToken);
    }

    public async Task<ConversationDocument?> FindDocumentAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.ConversationDocuments.IncludingDeactivated()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == documentId, cancellationToken);
    }

    public async Task<List<ConversationDocument>> FindConversationDocumentsAsync(
        Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return await ctx.ConversationDocuments.IncludingDeactivated()
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .ToListAsync(cancellationToken);
    }

    public async Task RevokePermissionAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        await ctx.UserPermissions.IncludingDeactivated()
            .Where(x => x.UserId == userId && x.PermissionId == permissionId)
            .ExecuteDeleteAsync(cancellationToken);

        ClearPermissionCache();
    }

    /// <summary>
    /// These helpers write grants through the DbContext, skipping the eviction
    /// <see cref="IPermissionService"/> performs, so a later test would see a stale grant set.
    /// </summary>
    private void ClearPermissionCache()
    {
        Factory.Services.GetRequiredService<IUserPermissionCache>().Clear();
    }

    private static async Task SeedTestUsersAsync(EnterpriseGptDbContext ctx)
    {
        var date = DateTimeOffset.UtcNow;
        ctx.Users.AddRange(
            new User
            {
                Id = TestUsers.AdminUserId,
                FirstName = "Admin",
                LastName = "Tester",
                Email = "admin.tester@example.com",
                DateCreated = date,
                DateModified = date
            },
            new User
            {
                Id = TestUsers.RegularUserId,
                FirstName = "Regular",
                LastName = "Tester",
                Email = "regular.tester@example.com",
                DateCreated = date,
                DateModified = date
            });
        ctx.UserPermissions.Add(new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = TestUsers.AdminUserId,
            PermissionId = PermissionIds.Administrator,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        });

        await ctx.SaveChangesAsync();
    }

    public async Task<Guid> AddDocumentSummaryAsync(
        Guid documentId, DocumentSources source, Guid modelId, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        var date = DateTimeOffset.UtcNow;

        BaseDocumentSummary<Guid> summary = source switch
        {
            DocumentSources.Conversation => new ConversationDocumentSummary { ConversationDocumentId = documentId },
            DocumentSources.Project => new ProjectDocumentSummary { ProjectDocumentId = documentId },
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown document source.")
        };

        summary.Id = Guid.NewGuid();
        summary.Text = "An integration-test summary.";
        summary.ModelId = modelId;
        summary.DeploymentName = "rr-gpt5.6-luna";
        summary.PromptVersion = SummarizationPrompts.PromptVersion;
        summary.InputTokens = 900;
        summary.OutputTokens = 120;
        summary.ModelCallCount = 1;
        summary.DateCreated = date;
        summary.DateModified = date;

        ctx.Add(summary);
        await ctx.SaveChangesAsync(cancellationToken);

        return summary.Id;
    }

    public async Task<BaseDocumentSummary<Guid>?> FindDocumentSummaryAsync(
        Guid id, DocumentSources source, CancellationToken cancellationToken = default)
    {
        using var ctx = CreateContext();

        return source switch
        {
            DocumentSources.Conversation => await ctx.ConversationDocumentSummaries.IncludingDeactivated()
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken),
            DocumentSources.Project => await ctx.ProjectDocumentSummaries.IncludingDeactivated()
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown document source.")
        };
    }
}
