using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Xunit;

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

    /// <summary>
    /// Gets the shared application factory. Populated once the container has started.
    /// </summary>
    public CustomWebApplicationFactory Factory { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EnterpriseGptTests"
        }.ConnectionString;

        Factory = new CustomWebApplicationFactory(connectionString);

        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        await ctx.Database.EnsureCreatedAsync();
        await SeedTestUsersAsync(ctx);
    }

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
            DisplayName = $"{name} display",
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
            .Where(x => x.Id != PermissionIds.Administrator)
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.McpServers
            .ExecuteDeleteAsync(cancellationToken);
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
