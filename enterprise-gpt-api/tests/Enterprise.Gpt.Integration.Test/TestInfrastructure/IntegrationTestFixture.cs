using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using Testcontainers.MsSql;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Settings;
using Xunit;
// Aliased rather than imported wholesale: Microsoft.Azure.Cosmos also declares Permission and
// User, which collide with the entity types this fixture seeds.
using CosmosClient = Microsoft.Azure.Cosmos.CosmosClient;
using PartitionKeyBuilder = Microsoft.Azure.Cosmos.PartitionKeyBuilder;
using QueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Collection fixture that owns the SQL Server 2025 Testcontainer and the shared
/// <see cref="CustomWebApplicationFactory"/> for the whole integration run. The 2025 image is
/// required because <c>Program.cs</c> pins <c>UseCompatibilityLevel(170)</c>, which older
/// engines reject. Creates the schema with <c>EnsureCreatedAsync</c> (applying the HasData
/// seeds) and adds the fixed <see cref="TestUsers"/> identities, including the admin's
/// <c>Administrator</c> permission.
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest")
            .Build();

    private readonly CosmosEmulatorContainer _cosmos = new();

    /// <summary>
    /// The permissions seeded by <c>PermissionConfiguration</c>. Resets must leave these in place: the
    /// schema is created once for the whole run, so deleting a seeded row would remove it permanently and
    /// every later test would run against a database missing a built-in permission.
    /// </summary>
    private static readonly Guid[] _builtInPermissionIds = [PermissionIds.Administrator, PermissionIds.UploadFile];

    /// <summary>
    /// Gets the shared application factory. Populated once the container has started.
    /// </summary>
    public CustomWebApplicationFactory Factory { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // Started together: the Cosmos emulator takes appreciably longer to become ready than SQL
        // Server, and running them in sequence adds that whole delay to every integration run.
        await Task.WhenAll(_container.StartAsync(), _cosmos.StartAsync());

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EnterpriseGptTests"
        }.ConnectionString;

        Factory = new CustomWebApplicationFactory(connectionString, _cosmos.ConnectionString);

        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        await ctx.Database.EnsureCreatedAsync();
        await SeedTestUsersAsync(ctx);

        await EnsureTranscriptContainerAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _container.DisposeAsync();
        await _cosmos.DisposeAsync();
    }

    /// <summary>
    /// Provisions the transcript container through the same bootstrap the application runs, so the
    /// tests exercise the partition key paths and indexing policy production actually gets.
    /// </summary>
    private async Task EnsureTranscriptContainerAsync()
    {
        var cosmosClient = Factory.Services.GetRequiredService<CosmosClient>();
        var cosmosOptions = Factory.Services.GetRequiredService<IOptions<CosmosOptions>>().Value;

        await CosmosContainerBootstrap.EnsureTranscriptContainerAsync(
            cosmosClient,
            cosmosOptions,
            NullLogger.Instance);
    }

    /// <summary>
    /// Deletes every document in the transcript container, leaving the container itself in place.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    public async Task ResetTranscriptsAsync(CancellationToken cancellationToken = default)
    {
        var cosmosClient = Factory.Services.GetRequiredService<CosmosClient>();
        var cosmosOptions = Factory.Services.GetRequiredService<IOptions<CosmosOptions>>().Value;
        var container = cosmosClient.GetDatabase(cosmosOptions.DatabaseId).GetContainer(cosmosOptions.TranscriptContainerId);

        // Deleting the container and recreating it would be simpler, but provisioning costs a
        // round trip per test; reading the identifiers back is cheaper at these volumes.
        var query = new QueryDefinition("SELECT c.id, c.userId, c.conversationId FROM c");
        using var iterator = container.GetItemQueryIterator<TranscriptDocumentKey>(query);

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            foreach (var key in page)
            {
                var partitionKey = new PartitionKeyBuilder()
                    .Add(key.UserId.ToString())
                    .Add(key.ConversationId.ToString())
                    .Build();

                await container.DeleteItemAsync<object>(key.Id, partitionKey, cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>
    /// The identity of one transcript document, projected so a reset can address it by its
    /// complete partition key.
    /// </summary>
    private sealed record TranscriptDocumentKey(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("userId")] Guid UserId,
        [property: JsonPropertyName("conversationId")] Guid ConversationId);

    /// <summary>
    /// Restores the model catalog to its post-seed baseline: removes every model except the
    /// seeded one and resets the seeded row's activation/default state. Called per test for
    /// isolation. The seeded model is otherwise treated as read-only by tests — mutating
    /// scenarios must target rows created via <see cref="AddModelAsync"/>, because this reset
    /// does not restore the seeded row's name or limits.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    public async Task ResetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await ClearConversationUsageAsync(ctx, cancellationToken);
        await ctx.Models
            .Where(x => x.Id != KnownIds.SeedModelId)
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Models
            .Where(x => x.Id == KnownIds.SeedModelId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null)
                .SetProperty(p => p.IsDefault, false), cancellationToken);
    }

    /// <summary>
    /// Inserts a model directly into the database, bypassing the API, for arranging read
    /// scenarios.
    /// </summary>
    /// <param name="name">The model name (also used for display name and description).</param>
    /// <param name="isDefault">Whether the model is the catalog default.</param>
    /// <param name="deactivated">Whether the model is soft-deleted.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted model.</returns>
    public async Task<Guid> AddModelAsync(
        string name, bool isDefault = false, bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

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

    /// <summary>
    /// Reads a model row straight from the database for state assertions the API does not
    /// expose (e.g. audit columns).
    /// </summary>
    /// <param name="id">The model id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<Model?> FindModelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Restores MCP servers, permissions, and grants to their post-seed baseline: removes every
    /// grant except the admin's <c>Administrator</c> one, every permission except the seeded
    /// <c>Administrator</c> row, and every MCP server. Deletes run in FK order (grants →
    /// permissions → servers). Called per test for isolation.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    public async Task ResetMcpServersAndPermissionsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await ctx.UserPermissions
            .Where(x => x.UserId != TestUsers.AdminUserId || x.PermissionId != PermissionIds.Administrator)
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions
            .Where(x => !_builtInPermissionIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions
            .Where(x => _builtInPermissionIds.Contains(x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null), cancellationToken);
        await ClearConversationUsageAsync(ctx, cancellationToken);
        await ctx.McpServers
            .ExecuteDeleteAsync(cancellationToken);

        ClearPermissionCache();
    }

    /// <summary>
    /// Inserts an MCP server directly into the database, bypassing the API, together with its
    /// linked same-named permission (unless <paramref name="withPermission"/> is off), for
    /// arranging read, grant, and cascade scenarios.
    /// </summary>
    /// <param name="name">The server name (also used for the linked permission's name).</param>
    /// <param name="authType">The auth type discriminator (1 = None, 2 = EntraIdOnBehalfOf).</param>
    /// <param name="scope">The on-behalf-of scope; only meaningful when <paramref name="authType"/> is 2.</param>
    /// <param name="deactivated">Whether the server (and its permission) are soft-deleted.</param>
    /// <param name="withPermission">Whether to insert the linked permission row.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted server and of its linked permission, when created.</returns>
    public async Task<(Guid ServerId, Guid? PermissionId)> AddMcpServerAsync(
        string name, int authType = 1, string? scope = null, bool deactivated = false,
        bool withPermission = true, CancellationToken cancellationToken = default)
    {
        using var serviceScope = Factory.Services.CreateScope();
        var ctx = serviceScope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

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

    /// <summary>
    /// Inserts a user-permission grant directly into the database, bypassing the API, for
    /// arranging revoke and cascade scenarios.
    /// </summary>
    /// <param name="userId">The id of the user receiving the grant.</param>
    /// <param name="permissionId">The id of the granted permission.</param>
    /// <param name="deactivated">Whether the grant is soft-deleted.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted grant row.</returns>
    public async Task<Guid> AddUserPermissionAsync(
        Guid userId, Guid permissionId, bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

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

    /// <summary>
    /// Reads an MCP server row straight from the database for state assertions the API does not
    /// expose (e.g. audit columns, soft-delete timestamps).
    /// </summary>
    /// <param name="id">The server id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<McpServer?> FindMcpServerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.McpServers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Reads a permission row straight from the database for state assertions the API does not
    /// expose (e.g. audit columns, soft-delete timestamps).
    /// </summary>
    /// <param name="id">The permission id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<Permission?> FindPermissionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.Permissions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Reads the permission linked to an MCP server straight from the database, for asserting
    /// the auto-created permission's state.
    /// </summary>
    /// <param name="mcpServerId">The owning MCP server id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when no linked permission exists.</returns>
    public async Task<Permission?> FindPermissionByMcpServerAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.Permissions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.McpServerId == mcpServerId, cancellationToken);
    }

    /// <summary>
    /// Reads every grant row for a user/permission pair straight from the database, including
    /// deactivated rows, for asserting revoke and re-grant history.
    /// </summary>
    /// <param name="userId">The granted user's id.</param>
    /// <param name="permissionId">The granted permission's id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked grant rows for the pair; empty when none exist.</returns>
    public async Task<List<UserPermission>> FindUserPermissionsAsync(
        Guid userId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.UserPermissions
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.PermissionId == permissionId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Restores users, permissions, and grants to their post-seed baseline: removes every user
    /// beyond the seeded system user and the two <see cref="TestUsers"/> identities, restores
    /// those two identities' profiles, and rebuilds the admin's <c>Administrator</c> grant.
    /// Also clears the fake directory. Deletes run in FK order (grants → permissions → users).
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    public async Task ResetUsersAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await ctx.UserPermissions.ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions
            .Where(x => !_builtInPermissionIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Permissions
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

    /// <summary>
    /// Inserts a permission directly into the database, bypassing the API, for arranging grant
    /// and default-provisioning scenarios.
    /// </summary>
    /// <param name="name">The permission name.</param>
    /// <param name="isDefault">Whether new users receive it automatically.</param>
    /// <param name="deactivated">Whether the permission is soft-deleted.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted permission.</returns>
    public async Task<Guid> AddPermissionAsync(
        string name, bool isDefault = false, bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

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

    /// <summary>
    /// Inserts a user directly into the database, bypassing the API, for arranging read, update,
    /// and deactivation scenarios.
    /// </summary>
    /// <param name="firstName">The user's first name.</param>
    /// <param name="lastName">The user's last name.</param>
    /// <param name="email">The user's email address.</param>
    /// <param name="deactivated">Whether the user is soft-deleted.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted user.</returns>
    public async Task<Guid> AddUserAsync(
        string firstName, string lastName, string email, bool deactivated = false,
        CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

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

    /// <summary>
    /// Reads a user row straight from the database, including its grants, for state assertions
    /// the API does not expose (e.g. soft-delete timestamps on cascaded grants).
    /// </summary>
    /// <param name="id">The user id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<User?> FindUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserPermissions)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Removes every project and conversation and, with them, every uploaded document and chunk. Called
    /// per test for isolation. Deletes run in FK order (project chunks → project documents →
    /// conversation chunks → conversation documents → conversations → projects); conversations carry an
    /// optional foreign key to a project, so they have to go before the projects do.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    public async Task ResetConversationsAndDocumentsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await ctx.ProjectDocumentChunks.ExecuteDeleteAsync(cancellationToken);
        await ctx.ProjectDocuments.ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocumentChunks.ExecuteDeleteAsync(cancellationToken);
        await ctx.ConversationDocuments.ExecuteDeleteAsync(cancellationToken);
        await ClearConversationUsageAsync(ctx, cancellationToken);
        await ctx.Conversations.ExecuteDeleteAsync(cancellationToken);
        await ctx.Projects.ExecuteDeleteAsync(cancellationToken);

        // Other test classes' resets clear grants wholesale, and the collection runs serially, so the
        // upload grants are restored here rather than assumed to have survived.
        await EnsureUploadFileGrantsAsync(ctx, cancellationToken);

        Factory.BlobStorage.Reset();
        await ResetTranscriptsAsync(cancellationToken);
        ClearPermissionCache();

    }

    /// <summary>
    /// Removes the usage audit rows, children first. Called from every reset that deletes a table
    /// they reference — conversations, models, and MCP servers alike — because the audit trail
    /// intentionally has no cascade behind it.
    /// </summary>
    /// <param name="ctx">The context to delete through.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    private static async Task ClearConversationUsageAsync(EnterpriseGptDbContext ctx, CancellationToken cancellationToken)
    {
        await ctx.ConversationUsageMcpServers.ExecuteDeleteAsync(cancellationToken);

        // Tool calls nest through a self-referencing key, and a set-based delete gives no ordering
        // guarantee over one, so the parent links are broken before the rows go.
        await ctx.ConversationUsageToolCalls
            .Where(x => x.ParentId != null)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ParentId, (Guid?)null), cancellationToken);
        await ctx.ConversationUsageToolCalls.ExecuteDeleteAsync(cancellationToken);

        await ctx.ConversationUsage.ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a usage audit row directly into the database, bypassing the API, for arranging the
    /// read scenarios that restore a conversation's last model and MCP selection.
    /// </summary>
    /// <param name="conversationId">The conversation the turn belongs to.</param>
    /// <param name="modelId">The model that served the turn.</param>
    /// <param name="dateCreated">When the turn finished; controls which turn counts as newest.</param>
    /// <param name="userId">The owner. Defaults to the regular test user.</param>
    /// <param name="mcpServerIds">The MCP servers attached to the turn.</param>
    /// <param name="kind">The kind of call. Defaults to a chat turn.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted usage row.</returns>
    public async Task<Guid> AddConversationUsageAsync(
        Guid conversationId, Guid modelId, DateTimeOffset dateCreated, Guid? userId = null,
        IReadOnlyCollection<Guid>? mcpServerIds = null, ConversationUsageKinds kind = ConversationUsageKinds.Chat,
        CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var usage = new ConversationUsage
        {
            ConversationId = conversationId,
            UserId = userId ?? TestUsers.RegularUserId,
            ModelId = modelId,
            ProviderId = KnownIds.SeedProviderId,
            DeploymentName = "integration-deployment",
            Kind = kind,
            Status = ConversationUsageStatuses.Completed,
            InputTokens = 10,
            OutputTokens = 5,
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
    /// Inserts a usage row carrying a nested tool-call tree, as one graph, exactly the way
    /// <c>ConversationService</c> writes it — which is what makes this exercise the self-referencing
    /// key and the sequential key generation rather than a hand-wired set of identifiers.
    /// </summary>
    /// <param name="conversationId">The conversation the turn belongs to.</param>
    /// <param name="modelId">The model that served the turn.</param>
    /// <param name="mcpServerId">The catalog server the MCP child call is attributed to.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted usage row.</returns>
    public async Task<Guid> AddConversationUsageWithToolCallsAsync(
        Guid conversationId, Guid modelId, Guid? mcpServerId = null, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var date = DateTimeOffset.UtcNow;
        var child = new ConversationUsageToolCall
        {
            Sequence = 0,
            Depth = 1,
            Kind = ConversationToolKinds.McpTool,
            ToolName = "Weather_forecast",
            Source = "Weather",
            McpServerId = mcpServerId,
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
            ToolCalls = [parent, child]
        };

        ctx.ConversationUsage.Add(usage);
        await ctx.SaveChangesAsync(cancellationToken);

        return usage.Id;
    }

    /// <summary>
    /// Reads back a usage row's tool calls, deepest last, for asserting on what was persisted.
    /// </summary>
    /// <param name="usageId">The usage row the calls belong to.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The tool calls, ordered by depth then report order.</returns>
    public async Task<List<ConversationUsageToolCall>> GetConversationUsageToolCallsAsync(
        Guid usageId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.ConversationUsageToolCalls
            .AsNoTracking()
            .Where(x => x.ConversationUsageId == usageId)
            .OrderBy(x => x.Depth)
            .ThenBy(x => x.Sequence)
            .ToListAsync(cancellationToken);
    }

    private static async Task EnsureUploadFileGrantsAsync(EnterpriseGptDbContext ctx, CancellationToken cancellationToken)
    {
        Guid[] userIds = [TestUsers.AdminUserId, TestUsers.RegularUserId];

        var granted = await ctx.UserPermissions
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

    /// <summary>
    /// Inserts a conversation directly into the database, bypassing the API, for arranging upload
    /// scenarios.
    /// </summary>
    /// <param name="userId">The owner. Defaults to the regular test user.</param>
    /// <param name="deactivated">Whether the conversation is soft-deleted.</param>
    /// <param name="projectId">The project the conversation belongs to, if any.</param>
    /// <param name="modelId">The model its most recent turn ran with, if any.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted conversation.</returns>
    public async Task<Guid> AddConversationAsync(
        Guid? userId = null, bool deactivated = false, Guid? projectId = null, Guid? modelId = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? TestUsers.RegularUserId,
            ProjectId = projectId,
            ModelId = modelId,
            Name = "Integration Conversation",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        };

        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(cancellationToken);

        return conversation.Id;
    }

    /// <summary>
    /// Inserts a project directly into the database, bypassing the API, for arranging upload, read and
    /// cascade scenarios.
    /// </summary>
    /// <param name="name">The project name. Must be unique per user among active projects.</param>
    /// <param name="userId">The owner. Defaults to the regular test user.</param>
    /// <param name="instructions">The project's standing instructions, if any.</param>
    /// <param name="deactivated">Whether the project is soft-deleted.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted project.</returns>
    public async Task<Guid> AddProjectAsync(
        string name = "Integration Project", Guid? userId = null, string? instructions = null,
        bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var date = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? TestUsers.RegularUserId,
            Name = name,
            Description = $"{name} description",
            Instructions = instructions,
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        };

        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync(cancellationToken);

        return project.Id;
    }

    /// <summary>
    /// Inserts a conversation document and its chunks directly into the database, with embeddings the
    /// caller chooses, for arranging retrieval scenarios.
    /// </summary>
    /// <param name="conversationId">The conversation the document belongs to.</param>
    /// <param name="name">The document's file name, which is what citations are built from.</param>
    /// <param name="chunks">The chunks to insert, with the vector each one should be found by.</param>
    /// <param name="deactivated">Whether the document is soft-deleted.</param>
    /// <param name="chunksDeactivated">Whether the chunks are soft-deleted while the document stays active.</param>
    /// <param name="userId">The owner. Defaults to the regular test user.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted document.</returns>
    /// <remarks>
    /// Chunks are written with hand-chosen vectors rather than through the ingestion pipeline, because
    /// retrieval tests need the distances the database computes to be predictable. Going through the
    /// real extractor and chunker would make the ranking depend on a fixture's prose.
    /// </remarks>
    public async Task<Guid> AddConversationDocumentAsync(
        Guid conversationId, string name, IReadOnlyList<SeedChunk> chunks, bool deactivated = false,
        bool chunksDeactivated = false, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var date = DateTimeOffset.UtcNow;
        var document = new ConversationDocument
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

    /// <summary>
    /// Inserts a project document and its chunks directly into the database, with embeddings the caller
    /// chooses, for arranging retrieval scenarios that span a project.
    /// </summary>
    /// <param name="projectId">The project the document belongs to.</param>
    /// <param name="name">The document's file name.</param>
    /// <param name="chunks">The chunks to insert, with the vector each one should be found by.</param>
    /// <param name="userId">The owner. Defaults to the regular test user.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted document.</returns>
    public async Task<Guid> AddProjectDocumentAsync(
        Guid projectId, string name, IReadOnlyList<SeedChunk> chunks, Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

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

    /// <summary>
    /// Reads a project row straight from the database for state assertions the API does not expose
    /// (e.g. soft-delete timestamps).
    /// </summary>
    /// <param name="id">The project id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<Project?> FindProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Reads a conversation row straight from the database, including soft-deleted ones, for asserting
    /// what a project cascade did to it.
    /// </summary>
    /// <param name="id">The conversation id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<Conversation?> FindConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Reads a project document by id, including soft-deleted ones.
    /// </summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<ProjectDocument?> FindProjectDocumentAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.ProjectDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == documentId, cancellationToken);
    }

    /// <summary>
    /// Reads the persisted chunks of a project document, ordered by their position in the document.
    /// </summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked chunk entities.</returns>
    public async Task<List<ProjectDocumentChunk>> FindProjectDocumentChunksAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.ProjectDocumentChunks
            .AsNoTracking()
            .Where(x => x.ProjectDocumentId == documentId)
            .OrderBy(x => x.Index)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the persisted chunks of a document, ordered by their position in the document.
    /// </summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked chunk entities.</returns>
    public async Task<List<ConversationDocumentChunk>> FindDocumentChunksAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.ConversationDocumentChunks
            .AsNoTracking()
            .Where(x => x.ConversationDocumentId == documentId)
            .OrderBy(x => x.Index)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a document by id.
    /// </summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<ConversationDocument?> FindDocumentAsync(
        Guid documentId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.ConversationDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == documentId, cancellationToken);
    }

    /// <summary>
    /// Revokes a user's grant of a permission, for arranging authorization-failure scenarios against
    /// permissions that are granted by default.
    /// </summary>
    /// <param name="userId">The user whose grant is revoked.</param>
    /// <param name="permissionId">The permission to revoke.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    public async Task RevokePermissionAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await ctx.UserPermissions
            .Where(x => x.UserId == userId && x.PermissionId == permissionId)
            .ExecuteDeleteAsync(cancellationToken);

        ClearPermissionCache();
    }

    /// <summary>
    /// Evicts the singleton permission cache. These helpers write grants straight through the
    /// DbContext, so the eviction <see cref="Enterprise.Gpt.Service.IPermissionService"/> performs on
    /// the production path never runs and a later test would authorize against a stale grant set.
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
}
