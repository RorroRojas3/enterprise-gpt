using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Service;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class McpEndpointsTests
{
    private readonly IMcpServerService _mcpServerService = Substitute.For<IMcpServerService>();

    private static McpServerDto CreateServerDto(string name = "docs-server")
    {
        return new McpServerDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            Url = "https://mcp.example.com/sse",
            AuthType = McpAuthTypes.None,
            Scope = null,
            PermissionId = Guid.NewGuid(),
            DateDeactivated = null
        };
    }

    [Fact]
    public async Task GetPermittedMcpServersAsync_ServiceReturnsServers_ReturnsOkWithPayload()
    {
        List<McpDto> expected = [new() { Id = Guid.NewGuid(), Name = "docs-server", Description = "docs" }];
        _mcpServerService.GetPermittedMcpServersAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await McpEndpoints.GetPermittedMcpServersAsync(_mcpServerService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetAllMcpServersAsync_ServiceReturnsServers_ReturnsOkWithPayload()
    {
        List<McpServerDto> expected = [CreateServerDto("aaa-active"), CreateServerDto("bbb-deactivated")];
        _mcpServerService.GetAllMcpServersAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await McpEndpoints.GetAllMcpServersAsync(_mcpServerService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetMcpServerAsync_ServiceReturnsServer_ReturnsOkWithPayload()
    {
        var expected = CreateServerDto();
        _mcpServerService.GetMcpServerAsync(expected.Id, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await McpEndpoints.GetMcpServerAsync(expected.Id, _mcpServerService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task CreateMcpServerAsync_ServiceCreatesServer_ReturnsCreatedWithLocation()
    {
        var request = new CreateMcpServerActionDto
        {
            Name = "new-server",
            Description = "A new MCP server.",
            Url = "https://mcp.example.com/new",
            AuthType = McpAuthTypes.None
        };
        var created = CreateServerDto("new-server");
        _mcpServerService.CreateMcpServerAsync(request, Arg.Any<CancellationToken>()).Returns(created);

        var result = await McpEndpoints.CreateMcpServerAsync(request, _mcpServerService, TestContext.Current.CancellationToken);

        Assert.Equal($"/api/mcps/{created.Id}", result.Location);
        Assert.Same(created, result.Value);
    }

    [Fact]
    public async Task UpdateMcpServerAsync_ServiceUpdatesServer_ReturnsOkWithPayload()
    {
        var id = Guid.NewGuid();
        var request = new UpdateMcpServerActionDto
        {
            Name = "renamed-server",
            Description = "An updated MCP server.",
            Url = "https://mcp.example.com/renamed",
            AuthType = McpAuthTypes.EntraIdOnBehalfOf,
            Scope = "api://client-id/access_as_user"
        };
        var updated = CreateServerDto("renamed-server");
        _mcpServerService.UpdateMcpServerAsync(id, request, Arg.Any<CancellationToken>()).Returns(updated);

        var result = await McpEndpoints.UpdateMcpServerAsync(id, request, _mcpServerService, TestContext.Current.CancellationToken);

        Assert.Same(updated, result.Value);
    }

    [Fact]
    public async Task DeactivateMcpServerAsync_ServiceDeactivatesServer_ReturnsNoContent()
    {
        var id = Guid.NewGuid();

        await McpEndpoints.DeactivateMcpServerAsync(id, _mcpServerService, TestContext.Current.CancellationToken);

        await _mcpServerService.Received(1).DeactivateMcpServerAsync(id, Arg.Any<CancellationToken>());
    }
}
