using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
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
    public void BuildMcpDtoExpression_CompiledDelegate_MapsUserFacingProperties()
    {
        var server = CreateServer();
        var projection = McpServerMapper.BuildMcpDtoExpression(Guid.NewGuid()).Compile();

        var dto = projection(server);

        Assert.Equal(server.Id, dto.Id);
        Assert.Equal(server.Name, dto.Name);
        Assert.Equal(server.Description, dto.Description);
        Assert.Equal(server.IconKey, dto.IconKey);
        Assert.False(dto.RequiresUserApiKey);
        Assert.False(dto.HasUserApiKey);
        Assert.Null(dto.ApiKeyHint);

        // `McpDto` carries no headers, for the reason it carries no url, auth type or scope:
        // they are connection details, and this projection is the one non-administrators read.
        // Asserted on the type rather than a value, so adding the property is what fails.
        Assert.Null(typeof(McpDto).GetProperty(nameof(McpServerDto.Headers)));
    }

    [Fact]
    public void BuildMcpDtoExpression_UserApiKeyServer_ReportsOnlyTheCallersOwnCredential()
    {
        var userId = Guid.NewGuid();
        var server = CreateServer();
        server.AuthType = McpAuthTypes.UserApiKey;
        server.UserCredentials =
        [
            CreateCredential(server.Id, Guid.NewGuid(), "9999"),
            CreateCredential(server.Id, userId, "abcd")
        ];

        var dto = McpServerMapper.BuildMcpDtoExpression(userId).Compile()(server);

        Assert.True(dto.RequiresUserApiKey);
        Assert.True(dto.HasUserApiKey);
        Assert.Equal("abcd", dto.ApiKeyHint);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BuildMcpDtoExpression_UnusableCredential_ReadsAsAbsent(bool rejected, bool deactivated)
    {
        var userId = Guid.NewGuid();
        var server = CreateServer();
        server.AuthType = McpAuthTypes.UserApiKey;

        var credential = CreateCredential(server.Id, userId, "abcd");
        credential.DateRejected = rejected ? DateTimeOffset.UtcNow : null;
        credential.DateDeactivated = deactivated ? DateTimeOffset.UtcNow : null;
        server.UserCredentials = [credential];

        var dto = McpServerMapper.BuildMcpDtoExpression(userId).Compile()(server);

        Assert.True(dto.RequiresUserApiKey);
        Assert.False(dto.HasUserApiKey);
        Assert.Null(dto.ApiKeyHint);
    }

    [Fact]
    public void MapToMcpCredentialStatusDto_RejectedCredential_ReportsNoKey()
    {
        var mcpServerId = Guid.NewGuid();
        var credential = CreateCredential(mcpServerId, Guid.NewGuid(), "abcd");
        credential.DateRejected = DateTimeOffset.UtcNow;

        var status = credential.MapToMcpCredentialStatusDto(mcpServerId);

        Assert.Equal(mcpServerId, status.McpServerId);
        Assert.False(status.HasApiKey);
        Assert.Null(status.ApiKeyHint);
        Assert.Null(status.DateModified);
    }

    private static UserMcpCredential CreateCredential(Guid mcpServerId, Guid userId, string hint)
    {
        return new UserMcpCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            McpServerId = mcpServerId,
            Ciphertext = "protected",
            ApiKeyHint = hint,
            DateCreated = DateTimeOffset.UtcNow,
            DateModified = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public void MapToMcpServerDto_Headers_CrossToTheAdministrativeShape()
    {
        var server = CreateServer();
        server.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-MCP-Readonly"] = "true"
        };

        Assert.Equal("true", Assert.Contains("X-MCP-Readonly", server.MapToMcpServerDto().Headers!));
        Assert.Equal(
            "true",
            Assert.Contains("X-MCP-Readonly", McpServerMapper.MapToMcpServerDtoExpression.Compile()(server).Headers!));
    }

    [Fact]
    public void FromUpdateMcpServerActionDtoToMcpServer_OmittedHeaders_ClearsThem()
    {
        var server = CreateServer();
        server.Headers = new Dictionary<string, string> { ["X-MCP-Readonly"] = "true" };

        // The PUT is a full representation: every field is assigned unconditionally, so an
        // omitted set clears rather than preserves.
        new UpdateMcpServerActionDto
        {
            Name = server.Name,
            Description = server.Description,
            Url = server.Url,
            AuthType = server.AuthType,
            Scope = server.Scope
        }.FromUpdateMcpServerActionDtoToMcpServer(server);

        Assert.Null(server.Headers);
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
