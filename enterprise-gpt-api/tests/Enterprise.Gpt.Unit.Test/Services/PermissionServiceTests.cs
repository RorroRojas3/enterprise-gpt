using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Permission;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class PermissionServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly PermissionService _service;

    public PermissionServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = new PermissionService(
            NullLogger<PermissionService>.Instance,
            _tokenService,
            _fixture.Context,
            new CreatePermissionActionDtoValidator(),
            new UpdatePermissionActionDtoValidator());
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<Permission> AddPermissionAsync(
        string name, Guid? mcpServerId = null, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            McpServerId = mcpServerId,
            DateDeactivated = dateDeactivated,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        };

        using var ctx = _fixture.CreateContext();
        ctx.Permissions.Add(permission);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return permission;
    }

    private async Task<McpServer> AddMcpServerAsync(string name, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            Url = $"https://mcp.example.com/{name}",
            AuthType = McpAuthTypes.None,
            DateDeactivated = dateDeactivated,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        };

        using var ctx = _fixture.CreateContext();
        ctx.McpServers.Add(server);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return server;
    }

    private async Task<User> AddUserAsync(DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Test",
            LastName = "User",
            Email = $"{Guid.NewGuid():N}@example.com",
            DateDeactivated = dateDeactivated,
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private async Task<UserPermission> AddGrantAsync(
        Guid permissionId, Guid userId, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var grant = new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionId = permissionId,
            DateDeactivated = dateDeactivated,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        };

        using var ctx = _fixture.CreateContext();
        ctx.UserPermissions.Add(grant);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return grant;
    }

    private async Task<List<UserPermission>> FindGrantsAsync(Guid userId, Guid permissionId)
    {
        using var ctx = _fixture.CreateContext();
        return await ctx.UserPermissions
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.PermissionId == permissionId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetPermissionsAsync_ActiveAndDeactivatedPermissionsExist_ReturnsOnlyActiveOrderedByName()
    {
        var active = await AddPermissionAsync("aaa-active");
        var deactivated = await AddPermissionAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        var result = await _service.GetPermissionsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result, x => x.Id == active.Id);
        Assert.Contains(result, x => x.Id == KnownIds.AdministratorPermissionId);
        Assert.DoesNotContain(result, x => x.Id == deactivated.Id);
        Assert.Equal(result.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Id), result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAllPermissionsAsync_DeactivatedPermissionExists_IncludesDeactivatedOrderedByName()
    {
        var deactivated = await AddPermissionAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        var result = await _service.GetAllPermissionsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result, x => x.Id == deactivated.Id);
        Assert.Contains(result, x => x.Id == KnownIds.AdministratorPermissionId);
        Assert.Equal(result.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Id), result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetPermissionAsync_ActivePermissionExists_ReturnsMappedDto()
    {
        var server = await AddMcpServerAsync("aaa-server");
        var permission = await AddPermissionAsync("aaa-single", server.Id);

        var result = await _service.GetPermissionAsync(permission.Id, TestContext.Current.CancellationToken);

        Assert.Equal(permission.Id, result.Id);
        Assert.Equal(permission.Name, result.Name);
        Assert.Equal(permission.Description, result.Description);
        Assert.Equal(server.Id, result.McpServerId);
        Assert.Null(result.DateDeactivated);
    }

    [Fact]
    public async Task GetPermissionAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetPermissionAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("Permission not found.", exception.Message);
    }

    [Fact]
    public async Task GetPermissionAsync_DeactivatedPermission_ThrowsNotFoundException()
    {
        var deactivated = await AddPermissionAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetPermissionAsync(deactivated.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreatePermissionAsync_ValidRequest_PersistsPermissionWithAudit()
    {
        var request = new CreatePermissionActionDto
        {
            Name = "aaa-created",
            Description = "A custom permission."
        };

        var result = await _service.CreatePermissionAsync(request, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(request.Name, result.Name);
        Assert.Null(result.McpServerId);

        using var ctx = _fixture.CreateContext();
        var persisted = await ctx.Permissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.Id, TestContext.Current.CancellationToken);
        Assert.Equal(request.Name, persisted.Name);
        Assert.Equal(request.Description, persisted.Description);
        Assert.Null(persisted.McpServerId);
        Assert.Equal(KnownIds.SeedUserId, persisted.CreatedById);
        Assert.Equal(KnownIds.SeedUserId, persisted.ModifiedById);
        Assert.Equal(persisted.DateCreated, persisted.DateModified);
        Assert.Null(persisted.DateDeactivated);
    }

    [Fact]
    public async Task CreatePermissionAsync_InvalidRequest_ThrowsValidationException()
    {
        var request = new CreatePermissionActionDto { Name = string.Empty };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreatePermissionAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreatePermissionAsync_NameUsedByActivePermission_ThrowsValidationException()
    {
        await AddPermissionAsync("aaa-taken");
        var request = new CreatePermissionActionDto { Name = "aaa-taken" };

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreatePermissionAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("already in use", exception.Message);
    }

    [Fact]
    public async Task CreatePermissionAsync_NameUsedByActiveMcpServer_ThrowsValidationException()
    {
        await AddMcpServerAsync("aaa-server-taken");
        var request = new CreatePermissionActionDto { Name = "aaa-server-taken" };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreatePermissionAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdatePermissionAsync_CustomPermission_UpdatesFieldsAndAudit()
    {
        var permission = await AddPermissionAsync("aaa-original");
        var request = new UpdatePermissionActionDto
        {
            Name = "aaa-renamed",
            Description = "An updated description."
        };

        var result = await _service.UpdatePermissionAsync(permission.Id, request, TestContext.Current.CancellationToken);

        Assert.Equal(request.Name, result.Name);
        Assert.Equal(request.Description, result.Description);

        using var ctx = _fixture.CreateContext();
        var persisted = await ctx.Permissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == permission.Id, TestContext.Current.CancellationToken);
        Assert.Equal(request.Name, persisted.Name);
        Assert.Equal(permission.DateCreated, persisted.DateCreated);
        Assert.Equal(KnownIds.SeedUserId, persisted.ModifiedById);
        Assert.True(persisted.DateModified > permission.DateModified);
    }

    [Fact]
    public async Task UpdatePermissionAsync_AdministratorPermission_ThrowsValidationException()
    {
        var request = new UpdatePermissionActionDto { Name = "aaa-renamed" };

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdatePermissionAsync(KnownIds.AdministratorPermissionId, request, TestContext.Current.CancellationToken));

        Assert.Contains("built-in Administrator permission cannot be modified", exception.Message);
    }

    [Fact]
    public async Task UpdatePermissionAsync_McpLinkedPermission_ThrowsValidationException()
    {
        var server = await AddMcpServerAsync("aaa-server");
        var permission = await AddPermissionAsync("aaa-server-permission", server.Id);
        var request = new UpdatePermissionActionDto { Name = "aaa-renamed" };

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdatePermissionAsync(permission.Id, request, TestContext.Current.CancellationToken));

        Assert.Contains("managed by its MCP server", exception.Message);
    }

    [Fact]
    public async Task UpdatePermissionAsync_NameUsedByOtherActivePermission_ThrowsValidationException()
    {
        await AddPermissionAsync("aaa-taken");
        var permission = await AddPermissionAsync("bbb-original");
        var request = new UpdatePermissionActionDto { Name = "aaa-taken" };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdatePermissionAsync(permission.Id, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdatePermissionAsync_UnknownId_ThrowsNotFoundException()
    {
        var request = new UpdatePermissionActionDto { Name = "aaa-renamed" };

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.UpdatePermissionAsync(Guid.NewGuid(), request, TestContext.Current.CancellationToken));

        Assert.Equal("Permission not found.", exception.Message);
    }

    [Fact]
    public async Task DeactivatePermissionAsync_CustomPermissionWithGrants_CascadesToGrants()
    {
        var permission = await AddPermissionAsync("aaa-cascade");
        var user = await AddUserAsync();
        var grant = await AddGrantAsync(permission.Id, user.Id);
        var revokedGrant = await AddGrantAsync(permission.Id, KnownIds.SeedUserId, dateDeactivated: DateTimeOffset.UtcNow.AddDays(-1));

        await _service.DeactivatePermissionAsync(permission.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var persistedPermission = await ctx.Permissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == permission.Id, TestContext.Current.CancellationToken);
        var persistedGrant = await ctx.UserPermissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == grant.Id, TestContext.Current.CancellationToken);
        var untouchedGrant = await ctx.UserPermissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == revokedGrant.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(persistedPermission.DateDeactivated);
        Assert.Equal(persistedPermission.DateDeactivated, persistedGrant.DateDeactivated);
        Assert.Equal(revokedGrant.DateDeactivated, untouchedGrant.DateDeactivated);
    }

    [Fact]
    public async Task DeactivatePermissionAsync_AdministratorPermission_ThrowsValidationException()
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => _service.DeactivatePermissionAsync(KnownIds.AdministratorPermissionId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivatePermissionAsync_McpLinkedPermission_ThrowsValidationException()
    {
        var server = await AddMcpServerAsync("aaa-server");
        var permission = await AddPermissionAsync("aaa-server-permission", server.Id);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.DeactivatePermissionAsync(permission.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivatePermissionAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivatePermissionAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("Permission not found.", exception.Message);
    }

    [Fact]
    public async Task GrantPermissionAsync_ActiveUserAndPermission_InsertsGrantWithAudit()
    {
        var permission = await AddPermissionAsync("aaa-grantable");
        var user = await AddUserAsync();

        await _service.GrantPermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken);

        var grants = await FindGrantsAsync(user.Id, permission.Id);
        var persisted = Assert.Single(grants);
        Assert.Equal(KnownIds.SeedUserId, persisted.CreatedById);
        Assert.Equal(KnownIds.SeedUserId, persisted.ModifiedById);
        Assert.Equal(persisted.DateCreated, persisted.DateModified);
        Assert.Null(persisted.DateDeactivated);
    }

    [Fact]
    public async Task GrantPermissionAsync_ActiveGrantAlreadyExists_IsIdempotentNoOp()
    {
        var permission = await AddPermissionAsync("aaa-grantable");
        var user = await AddUserAsync();
        await AddGrantAsync(permission.Id, user.Id);

        await _service.GrantPermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken);

        var grants = await FindGrantsAsync(user.Id, permission.Id);
        var persisted = Assert.Single(grants);
        Assert.Null(persisted.DateDeactivated);
    }

    [Fact]
    public async Task GrantPermissionAsync_UnknownUser_ThrowsNotFoundException()
    {
        var permission = await AddPermissionAsync("aaa-grantable");

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GrantPermissionAsync(Guid.NewGuid(), permission.Id, TestContext.Current.CancellationToken));

        Assert.Equal("User not found.", exception.Message);
    }

    [Fact]
    public async Task GrantPermissionAsync_DeactivatedUser_ThrowsNotFoundException()
    {
        var permission = await AddPermissionAsync("aaa-grantable");
        var user = await AddUserAsync(dateDeactivated: DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GrantPermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken));

        Assert.Equal("User not found.", exception.Message);
    }

    [Fact]
    public async Task GrantPermissionAsync_UnknownPermission_ThrowsNotFoundException()
    {
        var user = await AddUserAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GrantPermissionAsync(user.Id, Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("Permission not found.", exception.Message);
    }

    [Fact]
    public async Task GrantPermissionAsync_DeactivatedPermission_ThrowsNotFoundException()
    {
        var permission = await AddPermissionAsync("aaa-gone", dateDeactivated: DateTimeOffset.UtcNow);
        var user = await AddUserAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GrantPermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken));

        Assert.Equal("Permission not found.", exception.Message);
    }

    [Fact]
    public async Task RevokePermissionAsync_ActiveGrant_SetsDateDeactivated()
    {
        var permission = await AddPermissionAsync("aaa-revocable");
        var user = await AddUserAsync();
        await AddGrantAsync(permission.Id, user.Id);

        await _service.RevokePermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken);

        var grants = await FindGrantsAsync(user.Id, permission.Id);
        var persisted = Assert.Single(grants);
        Assert.NotNull(persisted.DateDeactivated);
        Assert.Equal(KnownIds.SeedUserId, persisted.ModifiedById);
    }

    [Fact]
    public async Task RevokePermissionAsync_NoActiveGrant_ThrowsNotFoundException()
    {
        var permission = await AddPermissionAsync("aaa-ungranted");
        var user = await AddUserAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.RevokePermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken));

        Assert.Equal("The user holds no active grant of this permission.", exception.Message);
    }

    [Fact]
    public async Task RevokePermissionAsync_CallerRevokingOwnAdministrator_ThrowsValidationException()
    {
        await AddGrantAsync(KnownIds.AdministratorPermissionId, KnownIds.SeedUserId);

        await Assert.ThrowsAsync<ValidationException>(() => _service.RevokePermissionAsync(
            KnownIds.SeedUserId, KnownIds.AdministratorPermissionId, TestContext.Current.CancellationToken));

        var grants = await FindGrantsAsync(KnownIds.SeedUserId, KnownIds.AdministratorPermissionId);
        Assert.Null(Assert.Single(grants).DateDeactivated);
    }

    [Fact]
    public async Task RevokePermissionAsync_AnotherUsersAdministrator_Succeeds()
    {
        var user = await AddUserAsync();
        await AddGrantAsync(KnownIds.AdministratorPermissionId, user.Id);

        await _service.RevokePermissionAsync(
            user.Id, KnownIds.AdministratorPermissionId, TestContext.Current.CancellationToken);

        var grants = await FindGrantsAsync(user.Id, KnownIds.AdministratorPermissionId);
        Assert.NotNull(Assert.Single(grants).DateDeactivated);
    }

    [Fact]
    public async Task GrantPermissionAsync_AfterRevoke_InsertsFreshRowLeavingRevokedHistory()
    {
        var permission = await AddPermissionAsync("aaa-regrant");
        var user = await AddUserAsync();
        await _service.GrantPermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken);
        await _service.RevokePermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken);

        await _service.GrantPermissionAsync(user.Id, permission.Id, TestContext.Current.CancellationToken);

        var grants = await FindGrantsAsync(user.Id, permission.Id);
        Assert.Equal(2, grants.Count);
        Assert.Equal(1, grants.Count(x => !x.DateDeactivated.HasValue));
    }
}
