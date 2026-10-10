using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;
using GraphUser = Microsoft.Graph.Models.User;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class UserServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IGraphService _graphService = Substitute.For<IGraphService>();
    private readonly IUserPermissionCache _permissionCache = Substitute.For<IUserPermissionCache>();
    private readonly UserService _service;

    public UserServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = new UserService(
            NullLogger<UserService>.Instance,
            _tokenService,
            _graphService,
            _fixture.Context,
            new CreateUserActionDtoValidator(),
            new UpdateUserActionDtoValidator(),
            _permissionCache);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private static GraphUser CreateGraphUser(
        Guid? id = null, string? givenName = "Ada", string? surname = "Lovelace",
        string? mail = "ada.lovelace@example.com", string? userPrincipalName = "ada.lovelace@example.onmicrosoft.com")
    {
        return new GraphUser
        {
            Id = (id ?? Guid.NewGuid()).ToString(),
            GivenName = givenName,
            Surname = surname,
            Mail = mail,
            UserPrincipalName = userPrincipalName
        };
    }

    private async Task<Permission> AddPermissionAsync(
        string name, bool isDefault = false, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            IsDefault = isDefault,
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

    private async Task<User> AddUserAsync(
        Guid? id = null, DateTimeOffset? dateDeactivated = null, string firstName = "Existing",
        string lastName = "User", string email = "existing.user@example.com",
        DateTimeOffset? created = null, Guid? revokedPermissionId = null,
        params Guid[] permissionIds)
    {
        var date = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = id ?? Guid.NewGuid(),
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            DateDeactivated = dateDeactivated,
            DateCreated = created ?? date,
            DateModified = created ?? date
        };

        foreach (var permissionId in permissionIds)
        {
            user.UserPermissions.Add(new UserPermission
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PermissionId = permissionId,
                DateCreated = date,
                CreatedById = KnownIds.SeedUserId,
                DateModified = date,
                ModifiedById = KnownIds.SeedUserId
            });
        }

        if (revokedPermissionId.HasValue)
        {
            user.UserPermissions.Add(new UserPermission
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PermissionId = revokedPermissionId.Value,
                DateCreated = date,
                CreatedById = KnownIds.SeedUserId,
                DateModified = date,
                ModifiedById = KnownIds.SeedUserId,
                DateDeactivated = date
            });
        }

        using var ctx = _fixture.CreateContext();
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private async Task<User?> FindUserAsync(Guid id)
    {
        using var ctx = _fixture.CreateContext();
        return await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserPermissions)
            .FirstOrDefaultAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Deactivates the seeded built-in default permission so a test can exercise the
    /// "no default permissions exist" case that the seed would otherwise make unreachable.
    /// </summary>
    private async Task DeactivateSeededDefaultsAsync()
    {
        using var ctx = _fixture.CreateContext();
        await ctx.Permissions
            .Where(p => p.IsDefault)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.DateDeactivated, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_UserAlreadyExists_ReturnsExistingWithoutCallingGraph()
    {
        var permission = await AddPermissionAsync("existing-permission");
        var oid = Guid.NewGuid();
        await AddUserAsync(oid, permissionIds: permission.Id);
        _tokenService.GetOid().Returns(oid);

        var (user, created) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Equal(oid, user.Id);
        Assert.Contains(user.Permissions, p => p.Id == permission.Id);
        await _graphService.DidNotReceiveWithAnyArgs().GetUserAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_ExistingUserMissingADefaultPermission_GrantsItOnSignIn()
    {
        // IsDefault is only consulted at provisioning time, so a default added after a user's first sign-in
        // would never reach them without this reconciliation.
        var oid = Guid.NewGuid();
        await AddUserAsync(oid);
        _tokenService.GetOid().Returns(oid);

        var (user, created) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Contains(user.Permissions, p => p.Id == KnownIds.UploadFilePermissionId);

        var persisted = await FindUserAsync(oid);
        Assert.NotNull(persisted);
        Assert.Single(persisted.UserPermissions, x => x.PermissionId == KnownIds.UploadFilePermissionId && !x.DateDeactivated.HasValue);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_ExistingUserAlreadyHoldingEveryDefault_DoesNotDuplicateGrants()
    {
        var oid = Guid.NewGuid();
        await AddUserAsync(oid, permissionIds: KnownIds.UploadFilePermissionId);
        _tokenService.GetOid().Returns(oid);

        await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        var persisted = await FindUserAsync(oid);
        Assert.NotNull(persisted);
        Assert.Single(persisted.UserPermissions, x => x.PermissionId == KnownIds.UploadFilePermissionId);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_ExistingUserWithARevokedDefault_DoesNotReinstateIt()
    {
        // A revoked grant is an administrator's decision; reconciliation must not undo it.
        var oid = Guid.NewGuid();
        await AddUserAsync(oid, permissionIds: KnownIds.UploadFilePermissionId);

        using (var ctx = _fixture.CreateContext())
        {
            await ctx.UserPermissions
                .Where(x => x.UserId == oid && x.PermissionId == KnownIds.UploadFilePermissionId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.DateDeactivated, DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        _tokenService.GetOid().Returns(oid);

        var (user, _) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(user.Permissions, p => p.Id == KnownIds.UploadFilePermissionId);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_NewUser_ProvisionsWithDefaultPermissionsOnly()
    {
        var defaultPermission = await AddPermissionAsync("default-permission", isDefault: true);
        await AddPermissionAsync("specialized-permission");
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>()).Returns(CreateGraphUser(oid));

        var (user, created) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.Equal(oid, user.Id);
        Assert.Equal("Ada", user.FirstName);
        Assert.Equal("ada.lovelace@example.com", user.Email);

        // The seeded built-in Upload File permission is itself a default, so it is granted alongside the
        // one this test adds; the specialized permission must not be.
        Assert.Equal(
            new[] { defaultPermission.Id, KnownIds.UploadFilePermissionId }.Order(),
            user.Permissions.Select(p => p.Id).Order());
        Assert.All(user.Permissions, p => Assert.True(p.IsDefault));

        var persisted = await FindUserAsync(oid);
        Assert.NotNull(persisted);
        Assert.Equal(
            new[] { defaultPermission.Id, KnownIds.UploadFilePermissionId }.Order(),
            persisted.UserPermissions.Select(p => p.PermissionId).Order());
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_NewUserAndNoDefaultPermissions_ProvisionsWithNoGrants()
    {
        await DeactivateSeededDefaultsAsync();
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>()).Returns(CreateGraphUser(oid));

        var (user, created) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.Empty(user.Permissions);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_DeactivatedDefaultPermission_IsNotGranted()
    {
        await DeactivateSeededDefaultsAsync();
        await AddPermissionAsync("stale-default", isDefault: true, dateDeactivated: DateTimeOffset.UtcNow);
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>()).Returns(CreateGraphUser(oid));

        var (user, _) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Empty(user.Permissions);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_GraphOmitsNames_PersistsEmptyStringsRatherThanNull()
    {
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(oid, givenName: null, surname: null));

        var (user, _) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, user.FirstName);
        Assert.Equal(string.Empty, user.LastName);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_GraphOmitsMail_FallsBackToUserPrincipalName()
    {
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>()).Returns(CreateGraphUser(oid, mail: null));

        var (user, _) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ada.lovelace@example.onmicrosoft.com", user.Email);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_GraphHasNoEmail_ThrowsNotFound()
    {
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(oid, mail: null, userPrincipalName: null));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_DeactivatedUser_ThrowsForbiddenWithoutReinserting()
    {
        var oid = Guid.NewGuid();
        await AddUserAsync(oid, dateDeactivated: DateTimeOffset.UtcNow);
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>()).Returns(CreateGraphUser(oid));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken));
        await _graphService.DidNotReceiveWithAnyArgs().GetUserAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_RacedByAConcurrentCall_ReturnsTheWinnersRow()
    {
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Simulates the concurrent caller committing between this call's existence check
                // and its own insert, which is what makes the insert collide on the primary key.
                using var ctx = _fixture.CreateContext();
                if (!ctx.Users.Any(x => x.Id == oid))
                {
                    var now = DateTimeOffset.UtcNow;
                    ctx.Users.Add(new User
                    {
                        Id = oid,
                        FirstName = "Winner",
                        LastName = "Race",
                        Email = "winner.race@example.com",
                        DateCreated = now,
                        DateModified = now
                    });
                    ctx.SaveChanges();
                }

                return CreateGraphUser(oid);
            });

        var (user, created) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Equal("Winner", user.FirstName);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_PreCreatedUser_ReturnsAdministratorAssignedPermissions()
    {
        var assigned = await AddPermissionAsync("assigned-permission");
        var oid = Guid.NewGuid();
        await AddUserAsync(oid, permissionIds: assigned.Id);
        _tokenService.GetOid().Returns(oid);

        var (user, created) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Contains(user.Permissions, p => p.Id == assigned.Id);
    }

    [Fact]
    public async Task CreateUserAsync_ValidRequest_ProvisionsWithRequestedAndDefaultPermissions()
    {
        var requested = await AddPermissionAsync("requested-permission");
        var defaultPermission = await AddPermissionAsync("default-permission", isDefault: true);
        var targetOid = Guid.NewGuid();
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [requested.Id]
        };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(targetOid, "Grace", "Hopper", request.Email));

        var user = await _service.CreateUserAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(targetOid, user.Id);
        Assert.Equal("Grace", user.FirstName);
        Assert.Equal(request.Email, user.Email);
        // The seeded built-in Upload File permission is a default, so it joins the requested one.
        Assert.Equal(
            new[] { requested.Id, defaultPermission.Id, KnownIds.UploadFilePermissionId }.Order(),
            user.Permissions.Select(p => p.Id).Order());

        var persisted = await FindUserAsync(targetOid);
        Assert.NotNull(persisted);
        Assert.Equal(3, persisted.UserPermissions.Count);
        Assert.All(persisted.UserPermissions, x => Assert.Equal(KnownIds.SeedUserId, x.CreatedById));
    }

    [Fact]
    public async Task CreateUserAsync_RequestedPermissionAlsoDefault_GrantsItOnce()
    {
        var permission = await AddPermissionAsync("default-permission", isDefault: true);
        var targetOid = Guid.NewGuid();
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [permission.Id]
        };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(targetOid, "Grace", "Hopper", request.Email));

        var user = await _service.CreateUserAsync(request, TestContext.Current.CancellationToken);

        Assert.Single(user.Permissions, p => p.Id == permission.Id);
    }

    [Fact]
    public async Task CreateUserAsync_ActiveUserAlreadyExists_ThrowsValidation()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid);
        var request = new CreateUserActionDto { Email = "grace.hopper@example.com" };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(targetOid, "Grace", "Hopper", request.Email));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateUserAsync_DeactivatedUserExists_ReactivatesRatherThanDuplicating()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, dateDeactivated: DateTimeOffset.UtcNow);
        var permission = await AddPermissionAsync("requested-permission");
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [permission.Id]
        };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(targetOid, "Grace", "Hopper", request.Email));

        var user = await _service.CreateUserAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(targetOid, user.Id);
        Assert.Equal("Grace", user.FirstName);

        var persisted = await FindUserAsync(targetOid);
        Assert.NotNull(persisted);
        Assert.Null(persisted.DateDeactivated);
        Assert.Single(persisted.UserPermissions, x => x.PermissionId == permission.Id);
    }

    [Fact]
    public async Task CreateUserAsync_DeactivatedUserWithSurvivingGrant_DoesNotDuplicateThatGrant()
    {
        var permission = await AddPermissionAsync("surviving-permission");
        var targetOid = Guid.NewGuid();
        // A grant left active by the pre-cascade DeactivateUserAsync: re-granting it would break
        // the filtered unique index on (UserId, PermissionId) WHERE DateDeactivated IS NULL.
        await AddUserAsync(targetOid, dateDeactivated: DateTimeOffset.UtcNow, permissionIds: permission.Id);
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [permission.Id]
        };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(targetOid, "Grace", "Hopper", request.Email));

        var user = await _service.CreateUserAsync(request, TestContext.Current.CancellationToken);

        Assert.Single(user.Permissions, p => p.Id == permission.Id);

        var persisted = await FindUserAsync(targetOid);
        Assert.NotNull(persisted);
        Assert.Single(persisted.UserPermissions, x => x.PermissionId == permission.Id && !x.DateDeactivated.HasValue);
    }

    [Fact]
    public async Task CreateUserAsync_AmbiguousDirectoryMatch_PropagatesValidation()
    {
        var request = new CreateUserActionDto { Email = "shared@example.com" };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new ValidationException("That email address matches more than one directory user."));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateUserAsync_DirectoryIdIsNotAGuid_ThrowsNotFound()
    {
        var request = new CreateUserActionDto { Email = "grace.hopper@example.com" };
        var graphUser = CreateGraphUser();
        graphUser.Id = "not-a-guid";
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>()).Returns(graphUser);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateUserAsync_ErrorMessages_DoNotLeakTheEmailAddress()
    {
        const string email = "grace.hopper@example.com";
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid);
        var request = new CreateUserActionDto { Email = email };
        _graphService.GetUserByEmailAsync(email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(targetOid, "Grace", "Hopper", email));

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(email, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateUserAsync_UnknownPermission_ThrowsNotFound()
    {
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [Guid.NewGuid()]
        };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(Guid.NewGuid(), "Grace", "Hopper", request.Email));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateUserAsync_DeactivatedPermissionRequested_ThrowsNotFound()
    {
        var permission = await AddPermissionAsync("stale-permission", dateDeactivated: DateTimeOffset.UtcNow);
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [permission.Id]
        };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(Guid.NewGuid(), "Grace", "Hopper", request.Email));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task CreateUserAsync_InvalidEmail_ThrowsValidationWithoutCallingGraph(string email)
    {
        var request = new CreateUserActionDto { Email = email };

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));
        await _graphService.DidNotReceiveWithAnyArgs().GetUserByEmailAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateUserAsync_GraphMiss_PropagatesNotFound()
    {
        var request = new CreateUserActionDto { Email = "nobody@example.com" };
        _graphService.GetUserByEmailAsync(request.Email, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new NotFoundException($"No directory user found for '{request.Email}'."));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.CreateUserAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateUserAsync_PermissionSetChanged_GrantsRevokesAndPreservesUnchanged()
    {
        var keep = await AddPermissionAsync("keep-permission");
        var drop = await AddPermissionAsync("drop-permission");
        var add = await AddPermissionAsync("add-permission");
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: [keep.Id, drop.Id]);
        var originalKeepGrant = (await FindUserAsync(targetOid))!.UserPermissions.Single(x => x.PermissionId == keep.Id);

        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = [keep.Id, add.Id]
        };

        var user = await _service.UpdateUserAsync(targetOid, request, TestContext.Current.CancellationToken);

        Assert.Equal("Grace", user.FirstName);
        Assert.Equal(request.Email, user.Email);
        Assert.Equal(
            new[] { keep.Id, add.Id }.OrderBy(x => x),
            user.Permissions.Select(p => p.Id).OrderBy(x => x));

        var persisted = await FindUserAsync(targetOid);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.UserPermissions.Single(x => x.PermissionId == drop.Id).DateDeactivated);
        Assert.Null(persisted.UserPermissions.Single(x => x.PermissionId == add.Id).DateDeactivated);

        var keptGrant = persisted.UserPermissions.Single(x => x.PermissionId == keep.Id);
        Assert.Null(keptGrant.DateDeactivated);
        Assert.Equal(originalKeepGrant.Id, keptGrant.Id);
        Assert.Equal(originalKeepGrant.DateCreated, keptGrant.DateCreated);
    }

    [Fact]
    public async Task UpdateUserAsync_EmptyPermissionIds_RevokesEveryGrant()
    {
        var permission = await AddPermissionAsync("doomed-permission");
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: permission.Id);
        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = []
        };

        var user = await _service.UpdateUserAsync(targetOid, request, TestContext.Current.CancellationToken);

        Assert.Empty(user.Permissions);

        var persisted = await FindUserAsync(targetOid);
        Assert.NotNull(persisted);
        Assert.NotNull(Assert.Single(persisted.UserPermissions).DateDeactivated);
    }

    [Fact]
    public async Task UpdateUserAsync_CallerRevokingOwnAdministrator_ThrowsValidation()
    {
        var callerOid = Guid.NewGuid();
        await AddUserAsync(callerOid, permissionIds: KnownIds.AdministratorPermissionId);
        _tokenService.GetOid().Returns(callerOid);

        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = []
        };

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.UpdateUserAsync(callerOid, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateUserAsync_CallerKeepingOwnAdministrator_Succeeds()
    {
        var callerOid = Guid.NewGuid();
        await AddUserAsync(callerOid, permissionIds: KnownIds.AdministratorPermissionId);
        _tokenService.GetOid().Returns(callerOid);

        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = [KnownIds.AdministratorPermissionId]
        };

        var user = await _service.UpdateUserAsync(callerOid, request, TestContext.Current.CancellationToken);

        Assert.Equal(KnownIds.AdministratorPermissionId, Assert.Single(user.Permissions).Id);
    }

    [Fact]
    public async Task UpdateUserAsync_RevokingAnotherUsersAdministrator_Succeeds()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: KnownIds.AdministratorPermissionId);

        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = []
        };

        var user = await _service.UpdateUserAsync(targetOid, request, TestContext.Current.CancellationToken);

        Assert.Empty(user.Permissions);
    }

    [Fact]
    public async Task UpdateUserAsync_UnknownUser_ThrowsNotFound()
    {
        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com"
        };

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UpdateUserAsync(Guid.NewGuid(), request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateUserAsync_UnknownPermission_ThrowsNotFound()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid);
        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = [Guid.NewGuid()]
        };

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UpdateUserAsync(targetOid, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateUserAsync_InvalidRequest_ThrowsValidation()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid);
        var request = new UpdateUserActionDto
        {
            FirstName = "",
            LastName = "Hopper",
            Email = "not-an-email"
        };

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.UpdateUserAsync(targetOid, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateUserAsync_ActiveUser_SoftDeletesUserAndCascadesGrants()
    {
        var permission = await AddPermissionAsync("granted-permission");
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: permission.Id);

        await _service.DeactivateUserAsync(targetOid, TestContext.Current.CancellationToken);

        var persisted = await FindUserAsync(targetOid);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.DateDeactivated);
        Assert.NotNull(Assert.Single(persisted.UserPermissions).DateDeactivated);
    }

    [Fact]
    public async Task DeactivateUserAsync_ActiveUser_EvictsTheirCachedPermissions()
    {
        // Authorization never checks whether a user is still active, so the grant cascade only
        // removes access once this eviction lands.
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: KnownIds.AdministratorPermissionId);

        await _service.DeactivateUserAsync(targetOid, TestContext.Current.CancellationToken);

        _permissionCache.Received(1).Invalidate(targetOid);
    }

    [Fact]
    public async Task UpdateUserAsync_PermissionSetChanged_EvictsTheTargetsCachedPermissions()
    {
        var permission = await AddPermissionAsync("update-evicts");
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: permission.Id);

        await _service.UpdateUserAsync(
            targetOid,
            new UpdateUserActionDto
            {
                FirstName = "Grace",
                LastName = "Hopper",
                Email = "grace.hopper@example.com",
                PermissionIds = []
            },
            TestContext.Current.CancellationToken);

        _permissionCache.Received(1).Invalidate(targetOid);
    }

    [Fact]
    public async Task CreateUserAsync_ValidRequest_EvictsTheTargetsCachedPermissions()
    {
        var oid = Guid.NewGuid();
        _graphService.GetUserByEmailAsync("new.user@example.com", Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(oid, "New", "User", "new.user@example.com"));

        await _service.CreateUserAsync(
            new CreateUserActionDto { Email = "new.user@example.com", PermissionIds = [] },
            TestContext.Current.CancellationToken);

        _permissionCache.Received(1).Invalidate(oid);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_ExistingUser_CachesExactlyTheGrantsItReturns()
    {
        // Pins the invariant that the warm-fill set matches what the filter's loader would read.
        var permission = await AddPermissionAsync("signin-cached");
        var oid = Guid.NewGuid();
        await AddUserAsync(oid, permissionIds: permission.Id);
        _tokenService.GetOid().Returns(oid);

        var (user, _) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        _permissionCache.Received(1).Set(
            oid,
            Arg.Any<long>(),
            Arg.Is<IEnumerable<Guid>>(ids => ids != null
                && ids.OrderBy(x => x).SequenceEqual(user.Permissions.Select(p => p.Id).OrderBy(x => x))));
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_NewUser_CachesExactlyTheGrantsItReturns()
    {
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);
        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>())
            .Returns(CreateGraphUser(oid, "New", "User", "new.user@example.com"));

        var (user, created) = await _service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.True(created);
        _permissionCache.Received(1).Set(
            oid,
            Arg.Any<long>(),
            Arg.Is<IEnumerable<Guid>>(ids => ids != null
                && ids.OrderBy(x => x).SequenceEqual(user.Permissions.Select(p => p.Id).OrderBy(x => x))));
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_RevokeCommittingMidRequest_IsNotUndoneByTheWarmFill()
    {
        // Drives the real cache and evicts from inside the Graph call — i.e. after the stamp is
        // captured but before the Set — so the Set must be skipped. Capturing the stamp any later
        // would republish the pre-revoke snapshot for a whole entry lifetime, and this test is the
        // only thing that catches that: substitute call ordering cannot, because the grant read
        // goes to the DbContext rather than to the cache.
        var oid = Guid.NewGuid();
        _tokenService.GetOid().Returns(oid);

        var cache = new UserPermissionCache(
            NullLogger<UserPermissionCache>.Instance,
            Options.Create(new UserPermissionCacheOptions()),
            TimeProvider.System);
        var service = new UserService(
            NullLogger<UserService>.Instance,
            _tokenService,
            _graphService,
            _fixture.Context,
            new CreateUserActionDtoValidator(),
            new UpdateUserActionDtoValidator(),
            cache);

        _graphService.GetUserAsync(oid, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cache.Invalidate(oid);
            return CreateGraphUser(oid, "Mid", "Flight", "mid.flight@example.com");
        });

        await service.GetOrCreateCurrentUserAsync(TestContext.Current.CancellationToken);

        var reloadedPermissionId = Guid.NewGuid();
        var cached = await cache.GetOrLoadAsync(
            oid,
            _ => Task.FromResult<IReadOnlyCollection<Guid>>([reloadedPermissionId]),
            TestContext.Current.CancellationToken);

        Assert.Equal([reloadedPermissionId], cached);
    }

    [Fact]
    public async Task DeactivateUserAsync_AdministratorGrant_IsAlsoDeactivated()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: KnownIds.AdministratorPermissionId);

        await _service.DeactivateUserAsync(targetOid, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stillAdmin = await ctx.UserPermissions
            .AsNoTracking()
            .AnyAsync(p => p.UserId == targetOid
                && p.PermissionId == KnownIds.AdministratorPermissionId
                && !p.DateDeactivated.HasValue, TestContext.Current.CancellationToken);

        Assert.False(stillAdmin);
    }

    [Fact]
    public async Task DeactivateUserAsync_UnknownUser_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.DeactivateUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateUserAsync_AlreadyDeactivatedUser_ThrowsNotFound()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, dateDeactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.DeactivateUserAsync(targetOid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateUserAsync_CallerDeactivatingThemselves_ThrowsValidation()
    {
        var callerOid = Guid.NewGuid();
        await AddUserAsync(callerOid);
        _tokenService.GetOid().Returns(callerOid);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.DeactivateUserAsync(callerOid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUserAsync_ActiveUser_ReturnsActiveGrantsOnly()
    {
        var permission = await AddPermissionAsync("granted-permission");
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, permissionIds: permission.Id);
        await _service.UpdateUserAsync(targetOid, new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = []
        }, TestContext.Current.CancellationToken);

        var user = await _service.GetUserAsync(targetOid, TestContext.Current.CancellationToken);

        Assert.Empty(user.Permissions);
    }

    [Fact]
    public async Task GetUserAsync_UnknownUser_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUserAsync_DeactivatedUser_ThrowsNotFound()
    {
        var targetOid = Guid.NewGuid();
        await AddUserAsync(targetOid, dateDeactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetUserAsync(targetOid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchUsersAsync_NameFragment_MatchesFirstNameLastNameAndEmail()
    {
        await AddUserAsync();
        var match = await AddUserAsync();
        using (var ctx = _fixture.CreateContext())
        {
            var tracked = await ctx.Users.FirstAsync(x => x.Id == match.Id, TestContext.Current.CancellationToken);
            tracked.FirstName = "Grace";
            tracked.LastName = "Hopper";
            tracked.Email = "grace.hopper@example.com";
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var byFirstName = await _service.SearchUsersAsync("Grace", cancellationToken: TestContext.Current.CancellationToken);
        var byLastName = await _service.SearchUsersAsync("Hopper", cancellationToken: TestContext.Current.CancellationToken);
        var byEmail = await _service.SearchUsersAsync("grace.hopper@", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(match.Id, Assert.Single(byFirstName.Items).Id);
        Assert.Equal(match.Id, Assert.Single(byLastName.Items).Id);
        Assert.Equal(match.Id, Assert.Single(byEmail.Items).Id);
    }

    [Fact]
    public async Task SearchUsersAsync_NoFilter_ExcludesDeactivatedUsersAndReportsPaging()
    {
        await AddUserAsync();
        await AddUserAsync();
        await AddUserAsync(dateDeactivated: DateTimeOffset.UtcNow);

        var response = await _service.SearchUsersAsync(null, skip: 0, take: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, response.TotalCount);
        Assert.Single(response.Items);
        Assert.Equal(1, response.PageSize);
        Assert.Equal(1, response.CurrentPage);
    }

    [Fact]
    public async Task SearchUsersAsync_SecondPage_ReportsComputedCurrentPage()
    {
        await AddUserAsync();
        await AddUserAsync();
        await AddUserAsync();

        var response = await _service.SearchUsersAsync(null, skip: 2, take: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, response.CurrentPage);
        Assert.Equal(2, response.PageSize);
    }

    [Fact]
    public async Task SearchUsersAsync_NonPositiveTake_ClampsInsteadOfDividingByZero()
    {
        await AddUserAsync();

        var response = await _service.SearchUsersAsync(null, skip: -5, take: 0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, response.PageSize);
        Assert.Equal(1, response.CurrentPage);
        Assert.Single(response.Items);
    }

    [Fact]
    public async Task SearchUsersAsync_NoOrderRequested_KeepsTheListingsOriginalOrder()
    {
        // The order this route had before it accepted one, pinned so a later change to the sorted
        // path cannot quietly move it: clients written against it are promised it unchanged. The
        // term is only here to hold the seeded row out of the assertion.
        await AddUserAsync(lastName: "Zulu", firstName: "Ada", email: "ada@ordercheck.example");
        await AddUserAsync(lastName: "Alpha", firstName: "Grace", email: "grace@ordercheck.example");

        var response = await _service.SearchUsersAsync("ordercheck", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Alpha", "Zulu"], response.Items.Select(x => x.LastName));
    }

    [Fact]
    public async Task SearchUsersAsync_SortByEmailDescending_ReversesTheOrder()
    {
        await AddUserAsync(email: "ada@mailcheck.example");
        await AddUserAsync(email: "zoe@mailcheck.example");

        var response = await _service.SearchUsersAsync(
            "mailcheck", sort: "email", dir: "desc", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("zoe@mailcheck.example", response.Items[0].Email);
    }

    [Fact]
    public async Task SearchUsersAsync_SortByCreatedAscending_PutsTheOldestRowFirst()
    {
        // The term holds the seeded row out: it is fixed at 2026-01-01, so an unfiltered
        // assertion would start failing on its own once `AddYears(-5)` passes that date.
        var oldest = await AddUserAsync(
            lastName: "Oldest", email: "oldest@agecheck.example",
            created: DateTimeOffset.UtcNow.AddYears(-5));
        await AddUserAsync(
            lastName: "Newest", email: "newest@agecheck.example",
            created: DateTimeOffset.UtcNow.AddDays(-1));

        var response = await _service.SearchUsersAsync(
            "agecheck", sort: "created", dir: "asc", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(oldest.Id, response.Items[0].Id);
    }

    [Fact]
    public async Task SearchUsersAsync_SortByNameDescending_ReversesBothKeys()
    {
        // The default is last name then first name, so reversing it has to reverse the second key
        // too or two namesakes come back in ascending first-name order inside a descending list.
        await AddUserAsync(lastName: "Hopper", firstName: "Ada");
        await AddUserAsync(lastName: "Hopper", firstName: "Grace");

        var response = await _service.SearchUsersAsync(
            "Hopper", sort: "name", dir: "desc", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Grace", "Ada"], response.Items.Select(x => x.FirstName));
    }

    [Fact]
    public async Task SearchUsersAsync_UnrecognisedSortField_ThrowsNamingTheParameter()
    {
        var failure = await Assert.ThrowsAsync<ValidationException>(() => _service.SearchUsersAsync(
            null, sort: "lastName", cancellationToken: TestContext.Current.CancellationToken));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("sort", error.PropertyName);
        Assert.Contains("'name'", error.ErrorMessage);
    }

    [Fact]
    public async Task SearchUsersAsync_UnrecognisedDirection_ThrowsNamingTheParameter()
    {
        var failure = await Assert.ThrowsAsync<ValidationException>(() => _service.SearchUsersAsync(
            null, sort: "name", dir: "sideways", cancellationToken: TestContext.Current.CancellationToken));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("dir", error.PropertyName);
    }

    [Fact]
    public async Task SearchUsersAsync_PermissionFilter_ReturnsOnlyItsHolders()
    {
        var permission = await AddPermissionAsync("Reports");
        var holder = await AddUserAsync(lastName: "Holder", permissionIds: permission.Id);
        await AddUserAsync(lastName: "Bystander");

        var response = await _service.SearchUsersAsync(
            null, permissionId: permission.Id, cancellationToken: TestContext.Current.CancellationToken);

        // The count travels with the filter: a total describing the whole directory would send the
        // numbered pager above it paging past the end of the result set.
        Assert.Equal(1, response.TotalCount);
        Assert.Equal(holder.Id, Assert.Single(response.Items).Id);
    }

    [Fact]
    public async Task SearchUsersAsync_PermissionFilter_ExcludesARevokedGrant()
    {
        // The grant carries its own DateDeactivated, so a revoked one still sits in the join table
        // and would match a filter that only looked at the permission id.
        var permission = await AddPermissionAsync("Reports");
        await AddUserAsync(lastName: "Revoked", revokedPermissionId: permission.Id);

        var response = await _service.SearchUsersAsync(
            null, permissionId: permission.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, response.TotalCount);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task SearchUsersAsync_PermissionFilter_ExcludesDeactivatedUsers()
    {
        var permission = await AddPermissionAsync("Reports");
        await AddUserAsync(
            dateDeactivated: DateTimeOffset.UtcNow, lastName: "Departed", permissionIds: permission.Id);

        var response = await _service.SearchUsersAsync(
            null, permissionId: permission.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, response.TotalCount);
    }

    [Fact]
    public async Task SearchUsersAsync_PermissionAndNameFilters_ApplyTogether()
    {
        var permission = await AddPermissionAsync("Reports");
        var match = await AddUserAsync(lastName: "Hopper", permissionIds: permission.Id);
        await AddUserAsync(lastName: "Hopper");
        await AddUserAsync(lastName: "Lovelace", permissionIds: permission.Id);

        var response = await _service.SearchUsersAsync(
            "Hopper", permissionId: permission.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, response.TotalCount);
        Assert.Equal(match.Id, Assert.Single(response.Items).Id);
    }

    [Fact]
    public async Task SearchUsersAsync_PermissionFilterOmitted_LeavesTheDirectoryUnfiltered()
    {
        var permission = await AddPermissionAsync("Reports");
        await AddUserAsync(lastName: "Holder", permissionIds: permission.Id);
        await AddUserAsync(lastName: "Bystander");

        var response = await _service.SearchUsersAsync(null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, response.TotalCount);
    }

    [Fact]
    public async Task SearchUsersAsync_UnknownPermissionId_ThrowsNamingTheParameter()
    {
        // A validation problem rather than the 404 every other permission-existence check raises: a
        // 404 on a list route says the directory is gone, while a filter control can render a 400.
        var failure = await Assert.ThrowsAsync<ValidationException>(() => _service.SearchUsersAsync(
            null, permissionId: Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("permissionId", error.PropertyName);
    }

    [Fact]
    public async Task SearchUsersAsync_DeactivatedPermissionId_IsRefusedWithTheAbsentOnes()
    {
        // The filter is offered from GET api/permissions, which returns neither absent nor
        // deactivated permissions, so accepting one would answer an empty page for a control that
        // could never have shown it.
        var retired = await AddPermissionAsync("Retired", dateDeactivated: DateTimeOffset.UtcNow);

        var failure = await Assert.ThrowsAsync<ValidationException>(() => _service.SearchUsersAsync(
            null, permissionId: retired.Id, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("permissionId", Assert.Single(failure.Errors).PropertyName);
    }

    [Fact]
    public async Task SearchUsersAsync_PermissionFilterWithSort_AppliesBoth()
    {
        var permission = await AddPermissionAsync("Reports");
        await AddUserAsync(lastName: "Zulu", permissionIds: permission.Id);
        await AddUserAsync(lastName: "Alpha", permissionIds: permission.Id);
        await AddUserAsync(lastName: "Mike");

        var response = await _service.SearchUsersAsync(
            null, permissionId: permission.Id, sort: "name",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, response.TotalCount);
        Assert.Equal(["Alpha", "Zulu"], response.Items.Select(x => x.LastName));
    }
}
