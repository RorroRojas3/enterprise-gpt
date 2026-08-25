using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class McpServerServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IMcpClientCache _mcpClientCache = Substitute.For<IMcpClientCache>();
    private readonly IUserPermissionCache _permissionCache = Substitute.For<IUserPermissionCache>();
    private readonly McpServerService _service;

    public McpServerServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = new McpServerService(
            NullLogger<McpServerService>.Instance,
            _tokenService,
            _fixture.Context,
            new CreateMcpServerActionDtoValidator(),
            new UpdateMcpServerActionDtoValidator(),
            _mcpClientCache,
            _permissionCache);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<McpServer> AddMcpServerAsync(
        string name, McpAuthTypes authType = McpAuthTypes.None, string? scope = null, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            Url = $"https://mcp.example.com/{name}",
            AuthType = authType,
            Scope = scope,
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

    private async Task<User> AddUserAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Other",
            LastName = "User",
            Email = $"{Guid.NewGuid():N}@example.com",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private async Task<UserPermission> AddGrantAsync(
        Guid permissionId, Guid? userId = null, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var grant = new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
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

    /// <summary>
    /// Seeds a server with its linked permission and, optionally, an active grant for the
    /// seed user — the shape <c>CreateMcpServerAsync</c> plus a grant would produce.
    /// </summary>
    private async Task<(McpServer Server, Permission Permission)> AddPermittedServerAsync(
        string name, bool granted = true)
    {
        var server = await AddMcpServerAsync(name);
        var permission = await AddPermissionAsync(name, server.Id);
        if (granted)
        {
            await AddGrantAsync(permission.Id);
        }

        return (server, permission);
    }

    private static CreateMcpServerActionDto CreateRequest(
        string name = "aaa-created", McpAuthTypes authType = McpAuthTypes.None, string? scope = null)
    {
        return new CreateMcpServerActionDto
        {
            Name = name,
            Description = $"{name} description",
            Url = $"https://mcp.example.com/{name}",
            AuthType = authType,
            Scope = scope
        };
    }

    private static UpdateMcpServerActionDto UpdateRequest(
        string name = "aaa-updated", McpAuthTypes authType = McpAuthTypes.None, string? scope = null)
    {
        return new UpdateMcpServerActionDto
        {
            Name = name,
            Description = $"{name} description",
            Url = $"https://mcp.example.com/{name}",
            AuthType = authType,
            Scope = scope
        };
    }

    [Fact]
    public async Task GetPermittedMcpServersAsync_ActiveGrantsExist_ReturnsOnlyGrantedServersOrderedByName()
    {
        var (second, _) = await AddPermittedServerAsync("bbb-permitted");
        var (first, _) = await AddPermittedServerAsync("aaa-permitted");
        var (ungranted, _) = await AddPermittedServerAsync("ccc-ungranted", granted: false);

        var result = await _service.GetPermittedMcpServersAsync(TestContext.Current.CancellationToken);

        Assert.Equal([first.Id, second.Id], result.Select(x => x.Id));
        Assert.DoesNotContain(result, x => x.Id == ungranted.Id);
        Assert.Equal(first.Name, result[0].Name);
        Assert.Equal(first.Description, result[0].Description);
    }

    [Fact]
    public async Task GetPermittedMcpServersAsync_RevokedGrant_ExcludesServer()
    {
        var server = await AddMcpServerAsync("aaa-revoked");
        var permission = await AddPermissionAsync("aaa-revoked", server.Id);
        await AddGrantAsync(permission.Id, dateDeactivated: DateTimeOffset.UtcNow);

        var result = await _service.GetPermittedMcpServersAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPermittedMcpServersAsync_GrantHeldByOtherUser_ExcludesServer()
    {
        var server = await AddMcpServerAsync("aaa-other-user");
        var permission = await AddPermissionAsync("aaa-other-user", server.Id);
        var otherUser = await AddUserAsync();
        await AddGrantAsync(permission.Id, userId: otherUser.Id);

        var result = await _service.GetPermittedMcpServersAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPermittedMcpServersAsync_DeactivatedServer_ExcludesServer()
    {
        var server = await AddMcpServerAsync("aaa-deactivated", dateDeactivated: DateTimeOffset.UtcNow);
        var permission = await AddPermissionAsync("aaa-deactivated", server.Id);
        await AddGrantAsync(permission.Id);

        var result = await _service.GetPermittedMcpServersAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPermittedMcpServersAsync_DeactivatedPermission_ExcludesServer()
    {
        var server = await AddMcpServerAsync("aaa-perm-deactivated");
        var permission = await AddPermissionAsync("aaa-perm-deactivated", server.Id, dateDeactivated: DateTimeOffset.UtcNow);
        await AddGrantAsync(permission.Id);

        var result = await _service.GetPermittedMcpServersAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllMcpServersAsync_DeactivatedServerExists_IncludesDeactivatedWithPermissionIdOrderedByName()
    {
        var (active, activePermission) = await AddPermittedServerAsync("bbb-active");
        var deactivated = await AddMcpServerAsync("aaa-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        var result = await _service.GetAllMcpServersAsync(TestContext.Current.CancellationToken);

        Assert.Equal([deactivated.Id, active.Id], result.Select(x => x.Id));
        Assert.NotNull(result[0].DateDeactivated);
        Assert.Equal(activePermission.Id, result[1].PermissionId);
    }

    [Fact]
    public async Task GetMcpServerAsync_ActiveServer_ReturnsMappedDtoWithPermissionId()
    {
        var server = await AddMcpServerAsync("aaa-single", McpAuthTypes.EntraIdOnBehalfOf, "api://client-id/access_as_user");
        var permission = await AddPermissionAsync("aaa-single", server.Id);

        var result = await _service.GetMcpServerAsync(server.Id, TestContext.Current.CancellationToken);

        Assert.Equal(server.Id, result.Id);
        Assert.Equal(server.Name, result.Name);
        Assert.Equal(server.Description, result.Description);
        Assert.Equal(server.Url, result.Url);
        Assert.Equal(server.AuthType, result.AuthType);
        Assert.Equal(server.Scope, result.Scope);
        Assert.Equal(permission.Id, result.PermissionId);
        Assert.Null(result.DateDeactivated);
    }

    [Fact]
    public async Task GetMcpServerAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetMcpServerAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("MCP server not found.", exception.Message);
    }

    [Fact]
    public async Task GetMcpServerAsync_DeactivatedServer_ThrowsNotFoundException()
    {
        var deactivated = await AddMcpServerAsync("aaa-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetMcpServerAsync(deactivated.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateMcpServerAsync_ValidRequest_PersistsServerAndLinkedPermissionWithAudit()
    {
        var request = CreateRequest("aaa-created");

        var result = await _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(request.Name, result.Name);
        Assert.NotNull(result.PermissionId);

        using var ctx = _fixture.CreateContext();
        var persistedServer = await ctx.McpServers
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.Id, TestContext.Current.CancellationToken);
        Assert.Equal(request.Name, persistedServer.Name);
        Assert.Equal(request.Description, persistedServer.Description);
        Assert.Equal(request.Url, persistedServer.Url);
        Assert.Equal(request.AuthType, persistedServer.AuthType);
        Assert.Equal(KnownIds.SeedUserId, persistedServer.CreatedById);
        Assert.Equal(KnownIds.SeedUserId, persistedServer.ModifiedById);
        Assert.Equal(persistedServer.DateCreated, persistedServer.DateModified);
        Assert.Null(persistedServer.DateDeactivated);

        var persistedPermission = await ctx.Permissions
            .AsNoTracking()
            .SingleAsync(x => x.McpServerId == result.Id, TestContext.Current.CancellationToken);
        Assert.Equal(result.PermissionId, persistedPermission.Id);
        Assert.Equal(request.Name, persistedPermission.Name);
        Assert.Equal($"Access to the {request.Name} MCP server.", persistedPermission.Description);
        Assert.Equal(KnownIds.SeedUserId, persistedPermission.CreatedById);
        Assert.Equal(KnownIds.SeedUserId, persistedPermission.ModifiedById);
        Assert.Null(persistedPermission.DateDeactivated);
    }

    [Fact]
    public async Task CreateMcpServerAsync_NameUsedByActiveServer_ThrowsValidationException()
    {
        await AddMcpServerAsync("aaa-taken");
        var request = CreateRequest("aaa-taken");

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("already in use", exception.Message);
    }

    [Fact]
    public async Task CreateMcpServerAsync_NameUsedByActivePermission_ThrowsValidationException()
    {
        await AddPermissionAsync("aaa-taken-permission");
        var request = CreateRequest("aaa-taken-permission");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateMcpServerAsync_NameUsedByDeactivatedServer_PersistsServer()
    {
        await AddMcpServerAsync("aaa-released", dateDeactivated: DateTimeOffset.UtcNow);
        var request = CreateRequest("aaa-released");

        var result = await _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(request.Name, result.Name);
    }

    [Fact]
    public async Task CreateMcpServerAsync_OnBehalfOfWithoutScope_ThrowsValidationException()
    {
        var request = CreateRequest("aaa-obo", McpAuthTypes.EntraIdOnBehalfOf, scope: null);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateMcpServerAsync_AuthTypeNoneWithScope_ThrowsValidationException()
    {
        var request = CreateRequest("aaa-none", McpAuthTypes.None, scope: "api://client-id/access_as_user");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMcpServerAsync_Rename_SyncsLinkedPermissionAndInvalidatesCache()
    {
        var server = await AddMcpServerAsync("aaa-original");
        var permission = await AddPermissionAsync("aaa-original", server.Id);
        var request = UpdateRequest("aaa-renamed");

        var result = await _service.UpdateMcpServerAsync(server.Id, request, TestContext.Current.CancellationToken);

        Assert.Equal(request.Name, result.Name);
        Assert.Equal(permission.Id, result.PermissionId);

        using var ctx = _fixture.CreateContext();
        var persistedServer = await ctx.McpServers
            .AsNoTracking()
            .SingleAsync(x => x.Id == server.Id, TestContext.Current.CancellationToken);
        Assert.Equal(request.Name, persistedServer.Name);
        Assert.Equal(request.Url, persistedServer.Url);
        Assert.Equal(KnownIds.SeedUserId, persistedServer.ModifiedById);
        Assert.True(persistedServer.DateModified > server.DateModified);

        var persistedPermission = await ctx.Permissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == permission.Id, TestContext.Current.CancellationToken);
        Assert.Equal(request.Name, persistedPermission.Name);
        Assert.Equal($"Access to the {request.Name} MCP server.", persistedPermission.Description);
        Assert.True(persistedPermission.DateModified > permission.DateModified);

        _mcpClientCache.Received(1).InvalidateServer(server.Id);
    }

    [Fact]
    public async Task UpdateMcpServerAsync_NameUnchanged_SkipsUniquenessCheckAndSucceeds()
    {
        var server = await AddMcpServerAsync("aaa-stable");
        await AddPermissionAsync("aaa-stable", server.Id);
        var request = UpdateRequest("aaa-stable");

        var result = await _service.UpdateMcpServerAsync(server.Id, request, TestContext.Current.CancellationToken);

        Assert.Equal("aaa-stable", result.Name);
    }

    [Fact]
    public async Task UpdateMcpServerAsync_NameUsedByOtherActiveServer_ThrowsValidationException()
    {
        await AddMcpServerAsync("aaa-taken");
        var server = await AddMcpServerAsync("bbb-original");
        var request = UpdateRequest("aaa-taken");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateMcpServerAsync(server.Id, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMcpServerAsync_NameUsedByCustomPermission_ThrowsValidationException()
    {
        await AddPermissionAsync("aaa-custom");
        var server = await AddMcpServerAsync("bbb-original");
        var request = UpdateRequest("aaa-custom");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateMcpServerAsync(server.Id, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMcpServerAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.UpdateMcpServerAsync(Guid.NewGuid(), UpdateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("MCP server not found.", exception.Message);
    }

    [Fact]
    public async Task DeactivateMcpServerAsync_ActiveServer_CascadesToPermissionAndGrantsAndInvalidatesCache()
    {
        var server = await AddMcpServerAsync("aaa-cascade");
        var permission = await AddPermissionAsync("aaa-cascade", server.Id);
        var grant = await AddGrantAsync(permission.Id);

        await _service.DeactivateMcpServerAsync(server.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var persistedServer = await ctx.McpServers
            .AsNoTracking()
            .SingleAsync(x => x.Id == server.Id, TestContext.Current.CancellationToken);
        var persistedPermission = await ctx.Permissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == permission.Id, TestContext.Current.CancellationToken);
        var persistedGrant = await ctx.UserPermissions
            .AsNoTracking()
            .SingleAsync(x => x.Id == grant.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(persistedServer.DateDeactivated);
        Assert.NotNull(persistedPermission.DateDeactivated);
        Assert.NotNull(persistedGrant.DateDeactivated);
        Assert.Equal(persistedServer.DateDeactivated, persistedPermission.DateDeactivated);
        Assert.Equal(persistedServer.DateDeactivated, persistedGrant.DateDeactivated);
        Assert.Equal(KnownIds.SeedUserId, persistedGrant.ModifiedById);

        _mcpClientCache.Received(1).InvalidateServer(server.Id);
        _permissionCache.Received(1).Invalidate(
            Arg.Is<IEnumerable<Guid>>(ids => ids != null && ids.SequenceEqual(new[] { grant.UserId })));
    }

    [Fact]
    public async Task DeactivateMcpServerAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateMcpServerAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("MCP server not found.", exception.Message);
    }

    [Fact]
    public async Task DeactivateMcpServerAsync_AlreadyDeactivatedServer_ThrowsNotFoundException()
    {
        var server = await AddMcpServerAsync("aaa-once");
        await AddPermissionAsync("aaa-once", server.Id);
        await _service.DeactivateMcpServerAsync(server.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateMcpServerAsync(server.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateMcpServerAsync_WithHeaders_RoundTripsThroughTheJsonConverter()
    {
        var request = CreateRequest("aaa-headers") with
        {
            Headers = new Dictionary<string, string>
            {
                ["X-MCP-Readonly"] = "true",
                ["X-MCP-Toolsets"] = "repos,wit,wiki"
            }
        };

        var result = await _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("true", Assert.Contains("X-MCP-Readonly", result.Headers!));

        // Read back through a fresh context so the value converter actually runs both ways
        // rather than the tracked instance being handed straight back.
        using var ctx = _fixture.CreateContext();
        var persisted = await ctx.McpServers
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, persisted.Headers!.Count);
        Assert.Equal("repos,wit,wiki", persisted.Headers["X-MCP-Toolsets"]);
        // Keyed case-insensitively on the way out, because HTTP header names are.
        Assert.Equal("true", persisted.Headers["x-mcp-readonly"]);
    }

    [Fact]
    public async Task UpdateMcpServerAsync_HeadersOnlyChange_IsPersistedAndInvalidatesCache()
    {
        var created = await _service.CreateMcpServerAsync(
            CreateRequest("aaa-hdr-edit") with
            {
                Headers = new Dictionary<string, string> { ["X-MCP-Toolsets"] = "repos" }
            },
            TestContext.Current.CancellationToken);

        var request = UpdateRequest("aaa-hdr-edit") with
        {
            Headers = new Dictionary<string, string> { ["X-MCP-Toolsets"] = "repos,wit,wiki" }
        };

        await _service.UpdateMcpServerAsync(created.Id, request, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var persisted = await ctx.McpServers
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);

        Assert.Equal("repos,wit,wiki", persisted.Headers!["X-MCP-Toolsets"]);

        // Headers decide the tool list, so a cached client must not outlive them.
        _mcpClientCache.Received(1).InvalidateServer(created.Id);
    }

    [Fact]
    public async Task UpdateMcpServerAsync_HeadersOmitted_ClearsThem()
    {
        var created = await _service.CreateMcpServerAsync(
            CreateRequest("aaa-hdr-clear") with
            {
                Headers = new Dictionary<string, string> { ["X-MCP-Readonly"] = "true" }
            },
            TestContext.Current.CancellationToken);

        // The PUT is a full representation, so an omitted set clears rather than preserves —
        // the shape `UpdateMcpServer_OmittedIconKey_ClearsIt` already pins for the icon. It is
        // load-bearing here: silently restoring a read-only server's write tools is the risk.
        await _service.UpdateMcpServerAsync(
            created.Id, UpdateRequest("aaa-hdr-clear"), TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var persisted = await ctx.McpServers
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);

        Assert.Null(persisted.Headers);
    }

    /// <summary>
    /// A mutation of the dictionary already on a tracked entity is saved, rather than being missed
    /// because the reference did not change.
    /// </summary>
    /// <remarks>
    /// This does <b>not</b> pin the explicit <c>ValueComparer</c>: EF Core 10 infers a structural,
    /// deep-snapshotting comparer for a <c>Dictionary&lt;string, string&gt;</c> even behind a value
    /// converter, so it passes with that argument deleted. The explicit comparer is kept for what
    /// inference does not give — ordinal ordering in equality and hashing, so the result cannot
    /// move with the ambient culture — and as insulation against that inference changing. What
    /// this test pins is the behaviour, which is the thing that must not regress.
    /// </remarks>
    [Fact]
    public async Task SaveChangesAsync_HeadersMutatedInPlace_IsDetectedAsAChange()
    {
        var created = await _service.CreateMcpServerAsync(
            CreateRequest("aaa-hdr-inplace") with
            {
                Headers = new Dictionary<string, string> { ["X-MCP-Toolsets"] = "repos" }
            },
            TestContext.Current.CancellationToken);

        using (var editContext = _fixture.CreateContext())
        {
            var tracked = await editContext.McpServers
                .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);

            tracked.Headers!["X-MCP-Toolsets"] = "repos,wit";

            await editContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var readContext = _fixture.CreateContext();
        var persisted = await readContext.McpServers
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.Id, TestContext.Current.CancellationToken);

        Assert.Equal("repos,wit", persisted.Headers!["X-MCP-Toolsets"]);
    }

    [Fact]
    public async Task CreateMcpServerAsync_ReservedHeaderName_ThrowsValidationException()
    {
        var request = CreateRequest("aaa-hdr-reserved") with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer nope" }
        };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateMcpServerAsync(request, TestContext.Current.CancellationToken));
    }
}
