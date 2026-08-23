using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Mappers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Mappers;

public class McpServerMapperTests
{
    private static McpServer CreateServer(DateTimeOffset? dateDeactivated = null)
    {
        return new McpServer
        {
            Id = Guid.NewGuid(),
            Name = "docs-server",
            Description = "A documentation MCP server.",
            Url = "https://mcp.example.com/sse",
            AuthType = McpAuthTypes.EntraIdOnBehalfOf,
            Scope = "api://client-id/access_as_user",
            IconKey = "microsoft",
            DateDeactivated = dateDeactivated,
            DateCreated = DateTimeOffset.UtcNow.AddDays(-1),
            DateModified = DateTimeOffset.UtcNow,
            CreatedById = Guid.NewGuid(),
            ModifiedById = Guid.NewGuid()
        };
    }

    [Fact]
    public void MapToMcpServerDto_PopulatedEntity_MapsAllProperties()
    {
        var server = CreateServer();
        var permissionId = Guid.NewGuid();

        var dto = server.MapToMcpServerDto(permissionId);

        Assert.Equal(server.Id, dto.Id);
        Assert.Equal(server.Name, dto.Name);
        Assert.Equal(server.Description, dto.Description);
        Assert.Equal(server.Url, dto.Url);
        Assert.Equal(server.AuthType, dto.AuthType);
        Assert.Equal(server.Scope, dto.Scope);
        Assert.Equal(server.IconKey, dto.IconKey);
        Assert.Equal(permissionId, dto.PermissionId);
        Assert.Equal(server.DateDeactivated, dto.DateDeactivated);
    }

    [Fact]
    public void MapToMcpServerDto_NoPermissionId_LeavesPermissionIdNull()
    {
        var server = CreateServer();

        var dto = server.MapToMcpServerDto();

        Assert.Null(dto.PermissionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapToMcpServerDto_DateDeactivated_RoundTrips(bool deactivated)
    {
        var dateDeactivated = deactivated ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        var server = CreateServer(dateDeactivated);

        var dto = server.MapToMcpServerDto();

        Assert.Equal(dateDeactivated, dto.DateDeactivated);
    }

    [Fact]
    public void MapToMcpServerDtoExpression_CompiledDelegate_ResolvesActivePermissionId()
    {
        var server = CreateServer();
        var activePermission = new Permission
        {
            Id = Guid.NewGuid(),
            Name = server.Name,
            McpServerId = server.Id
        };
        var deactivatedPermission = new Permission
        {
            Id = Guid.NewGuid(),
            Name = $"{server.Name}-old",
            McpServerId = server.Id,
            DateDeactivated = DateTimeOffset.UtcNow
        };
        server.Permissions = [deactivatedPermission, activePermission];
        var projection = McpServerMapper.MapToMcpServerDtoExpression.Compile();

        var dto = projection(server);

        Assert.Equal(server.MapToMcpServerDto(activePermission.Id), dto);
    }

    [Fact]
    public void MapToMcpServerDtoExpression_NoActivePermission_LeavesPermissionIdNull()
    {
        var server = CreateServer();
        var projection = McpServerMapper.MapToMcpServerDtoExpression.Compile();

        var dto = projection(server);

        Assert.Null(dto.PermissionId);
    }

    [Fact]
    public void MapToMcpDtoExpression_CompiledDelegate_MapsUserFacingProperties()
    {
        var server = CreateServer();
        var projection = McpServerMapper.MapToMcpDtoExpression.Compile();

        var dto = projection(server);

        Assert.Equal(server.Id, dto.Id);
        Assert.Equal(server.Name, dto.Name);
        Assert.Equal(server.Description, dto.Description);
        Assert.Equal(server.IconKey, dto.IconKey);
    }

    [Fact]
    public void FromCreateMcpServerActionDtoToMcpServer_ValidRequest_MapsRequestFieldsAndLeavesAuditDefaults()
    {
        var request = new CreateMcpServerActionDto
        {
            Name = "new-server",
            Description = "A new MCP server.",
            Url = "https://mcp.example.com/new",
            AuthType = McpAuthTypes.None,
            Scope = null,
            IconKey = "context7"
        };

        var server = request.FromCreateMcpServerActionDtoToMcpServer();

        Assert.Equal(request.Name, server.Name);
        Assert.Equal(request.Description, server.Description);
        Assert.Equal(request.Url, server.Url);
        Assert.Equal(request.AuthType, server.AuthType);
        Assert.Null(server.Scope);
        Assert.Equal(request.IconKey, server.IconKey);
        Assert.Equal(Guid.Empty, server.Id);
        Assert.Equal(default, server.DateCreated);
        Assert.Equal(default, server.DateModified);
        Assert.Equal(Guid.Empty, server.CreatedById);
        Assert.Equal(Guid.Empty, server.ModifiedById);
        Assert.Null(server.DateDeactivated);
        Assert.Empty(server.Permissions);
    }

    [Fact]
    public void FromUpdateMcpServerActionDtoToMcpServer_ExistingEntity_MutatesInPlaceAndPreservesAudit()
    {
        var server = CreateServer();
        var originalId = server.Id;
        var originalDateCreated = server.DateCreated;
        var originalCreatedById = server.CreatedById;
        var request = new UpdateMcpServerActionDto
        {
            Name = "renamed-server",
            Description = "An updated MCP server.",
            Url = "https://mcp.example.com/renamed",
            AuthType = McpAuthTypes.None,
            Scope = null,
            IconKey = null
        };

        request.FromUpdateMcpServerActionDtoToMcpServer(server);

        Assert.Equal(request.Name, server.Name);
        Assert.Equal(request.Description, server.Description);
        Assert.Equal(request.Url, server.Url);
        Assert.Equal(request.AuthType, server.AuthType);
        Assert.Null(server.Scope);
        // The PUT is a full representation, not a patch: an omitted icon clears the stored one.
        Assert.Null(server.IconKey);
        Assert.Equal(originalId, server.Id);
        Assert.Equal(originalDateCreated, server.DateCreated);
        Assert.Equal(originalCreatedById, server.CreatedById);
        Assert.Null(server.DateDeactivated);
    }
}
