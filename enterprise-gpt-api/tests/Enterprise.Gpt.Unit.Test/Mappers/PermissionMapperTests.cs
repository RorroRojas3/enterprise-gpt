using Enterprise.Gpt.Dto.Actions.Permission;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Mappers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Mappers;

public class PermissionMapperTests
{
    private static Permission CreatePermission(
        Guid? mcpServerId = null, DateTimeOffset? dateDeactivated = null)
    {
        return new Permission
        {
            Id = Guid.NewGuid(),
            Name = "docs-permission",
            Description = "Grants access to docs.",
            McpServerId = mcpServerId,
            DateDeactivated = dateDeactivated,
            DateCreated = DateTimeOffset.UtcNow.AddDays(-1),
            DateModified = DateTimeOffset.UtcNow,
            CreatedById = Guid.NewGuid(),
            ModifiedById = Guid.NewGuid()
        };
    }

    [Fact]
    public void MapToPermissionDto_PopulatedEntity_MapsAllProperties()
    {
        var permission = CreatePermission(mcpServerId: Guid.NewGuid(), dateDeactivated: DateTimeOffset.UtcNow);

        var dto = permission.MapToPermissionDto();

        Assert.Equal(permission.Id, dto.Id);
        Assert.Equal(permission.Name, dto.Name);
        Assert.Equal(permission.Description, dto.Description);
        Assert.Equal(permission.McpServerId, dto.McpServerId);
        Assert.Equal(permission.DateDeactivated, dto.DateDeactivated);
    }

    [Fact]
    public void MapToPermissionDto_CustomPermission_LeavesMcpServerIdNull()
    {
        var permission = CreatePermission();

        var dto = permission.MapToPermissionDto();

        Assert.Null(dto.McpServerId);
        Assert.Null(dto.DateDeactivated);
    }

    [Fact]
    public void MapToPermissionDtoExpression_CompiledDelegate_MatchesMapToPermissionDto()
    {
        var permission = CreatePermission(mcpServerId: Guid.NewGuid(), dateDeactivated: DateTimeOffset.UtcNow);
        var projection = PermissionMapper.MapToPermissionDtoExpression.Compile();

        var dto = projection(permission);

        Assert.Equal(permission.MapToPermissionDto(), dto);
    }

    [Fact]
    public void FromCreatePermissionActionDtoToPermission_ValidRequest_MapsRequestFieldsAndLeavesAuditDefaults()
    {
        var request = new CreatePermissionActionDto
        {
            Name = "new-permission",
            Description = "A new custom permission."
        };

        var permission = request.FromCreatePermissionActionDtoToPermission();

        Assert.Equal(request.Name, permission.Name);
        Assert.Equal(request.Description, permission.Description);
        Assert.Null(permission.McpServerId);
        Assert.Equal(Guid.Empty, permission.Id);
        Assert.Equal(default, permission.DateCreated);
        Assert.Equal(default, permission.DateModified);
        Assert.Equal(Guid.Empty, permission.CreatedById);
        Assert.Equal(Guid.Empty, permission.ModifiedById);
        Assert.Null(permission.DateDeactivated);
    }

    [Fact]
    public void FromUpdatePermissionActionDtoToPermission_ExistingEntity_MutatesInPlaceAndPreservesAudit()
    {
        var permission = CreatePermission(mcpServerId: Guid.NewGuid());
        var originalId = permission.Id;
        var originalMcpServerId = permission.McpServerId;
        var originalDateCreated = permission.DateCreated;
        var originalCreatedById = permission.CreatedById;
        var request = new UpdatePermissionActionDto
        {
            Name = "renamed-permission",
            Description = "An updated description."
        };

        request.FromUpdatePermissionActionDtoToPermission(permission);

        Assert.Equal(request.Name, permission.Name);
        Assert.Equal(request.Description, permission.Description);
        Assert.Equal(originalId, permission.Id);
        Assert.Equal(originalMcpServerId, permission.McpServerId);
        Assert.Equal(originalDateCreated, permission.DateCreated);
        Assert.Equal(originalCreatedById, permission.CreatedById);
        Assert.Null(permission.DateDeactivated);
    }
}
