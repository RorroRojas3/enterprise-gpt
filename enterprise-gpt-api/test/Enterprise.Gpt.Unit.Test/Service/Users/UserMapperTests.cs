using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Mappers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Mappers;

public class UserMapperTests
{
    private static User CreateUser(params UserPermission[] userPermissions)
    {
        var date = DateTimeOffset.UtcNow;
        return new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada.lovelace@example.com",
            DateCreated = date.AddDays(-1),
            DateModified = date,
            UserPermissions = [.. userPermissions]
        };
    }

    private static UserPermission CreateGrant(
        string name = "docs-permission", bool isDefault = false, DateTimeOffset? dateDeactivated = null)
    {
        var permissionId = Guid.NewGuid();
        var date = DateTimeOffset.UtcNow;
        return new UserPermission
        {
            Id = Guid.NewGuid(),
            PermissionId = permissionId,
            Permission = new Permission
            {
                Id = permissionId,
                Name = name,
                Description = $"{name} description",
                IsDefault = isDefault
            },
            DateDeactivated = dateDeactivated,
            DateCreated = date,
            DateModified = date
        };
    }

    [Fact]
    public void MapToUserDto_PopulatedEntity_MapsProfileAndPermissions()
    {
        var grant = CreateGrant(isDefault: true);
        var user = CreateUser(grant);

        var dto = user.MapToUserDto();

        Assert.Equal(user.Id, dto.Id);
        Assert.Equal(user.FirstName, dto.FirstName);
        Assert.Equal(user.LastName, dto.LastName);
        Assert.Equal(user.Email, dto.Email);
        Assert.Equal("Ada Lovelace", dto.FullName);
        var permission = Assert.Single(dto.Permissions);
        Assert.Equal(grant.PermissionId, permission.Id);
        Assert.Equal(grant.Permission.Name, permission.Name);
        Assert.Equal(grant.Permission.Description, permission.Description);
        Assert.True(permission.IsDefault);
    }

    [Fact]
    public void MapToUserDto_MixedGrants_ExcludesDeactivatedGrants()
    {
        var active = CreateGrant("active-permission");
        var revoked = CreateGrant("revoked-permission", dateDeactivated: DateTimeOffset.UtcNow);
        var user = CreateUser(active, revoked);

        var dto = user.MapToUserDto();

        var permission = Assert.Single(dto.Permissions);
        Assert.Equal(active.PermissionId, permission.Id);
    }

    [Fact]
    public void MapToUserDto_NoGrants_ReturnsEmptyPermissions()
    {
        var user = CreateUser();

        var dto = user.MapToUserDto();

        Assert.Empty(dto.Permissions);
    }

    [Fact]
    public void MapToUserDtoExpression_CompiledDelegate_MatchesMapToUserDto()
    {
        var user = CreateUser(
            CreateGrant("active-permission", isDefault: true),
            CreateGrant("revoked-permission", dateDeactivated: DateTimeOffset.UtcNow));
        var projection = UserMapper.MapToUserDtoExpression.Compile();

        var projected = projection(user);
        var mapped = user.MapToUserDto();

        // Compared member-wise rather than with record equality: UserDto.Permissions is an
        // IReadOnlyList, so the compiler-generated Equals falls back to reference equality on it.
        Assert.Equal(mapped.Id, projected.Id);
        Assert.Equal(mapped.FirstName, projected.FirstName);
        Assert.Equal(mapped.LastName, projected.LastName);
        Assert.Equal(mapped.Email, projected.Email);
        Assert.Equal(mapped.Permissions, projected.Permissions);
    }

    [Fact]
    public void FromUpdateUserActionDtoToUser_ExistingEntity_MutatesProfileAndPreservesIdentityAndGrants()
    {
        var user = CreateUser(CreateGrant());
        var originalId = user.Id;
        var originalDateCreated = user.DateCreated;
        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = [Guid.NewGuid()]
        };

        request.FromUpdateUserActionDtoToUser(user);

        Assert.Equal(request.FirstName, user.FirstName);
        Assert.Equal(request.LastName, user.LastName);
        Assert.Equal(request.Email, user.Email);
        Assert.Equal(originalId, user.Id);
        Assert.Equal(originalDateCreated, user.DateCreated);
        Assert.Single(user.UserPermissions);
        Assert.Null(user.DateDeactivated);
    }
}
