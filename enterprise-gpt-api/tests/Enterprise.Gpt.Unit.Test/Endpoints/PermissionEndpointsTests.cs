using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Permission;
using Enterprise.Gpt.Service;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class PermissionEndpointsTests
{
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();

    private static PermissionDto CreateDto(string name = "docs-permission")
    {
        return new PermissionDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            McpServerId = null,
            DateDeactivated = null
        };
    }

    [Fact]
    public async Task GetPermissionsAsync_ServiceReturnsPermissions_ReturnsOkWithPayload()
    {
        List<PermissionDto> expected = [CreateDto()];
        _permissionService.GetPermissionsAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await PermissionEndpoints.GetPermissionsAsync(_permissionService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetAllPermissionsAsync_ServiceReturnsPermissions_ReturnsOkWithPayload()
    {
        List<PermissionDto> expected = [CreateDto("aaa-active"), CreateDto("bbb-deactivated")];
        _permissionService.GetAllPermissionsAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await PermissionEndpoints.GetAllPermissionsAsync(_permissionService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetPermissionAsync_ServiceReturnsPermission_ReturnsOkWithPayload()
    {
        var expected = CreateDto();
        _permissionService.GetPermissionAsync(expected.Id, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await PermissionEndpoints.GetPermissionAsync(expected.Id, _permissionService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task CreatePermissionAsync_ServiceCreatesPermission_ReturnsCreatedWithLocation()
    {
        var request = new CreatePermissionActionDto
        {
            Name = "new-permission",
            Description = "A new custom permission."
        };
        var created = CreateDto("new-permission");
        _permissionService.CreatePermissionAsync(request, Arg.Any<CancellationToken>()).Returns(created);

        var result = await PermissionEndpoints.CreatePermissionAsync(request, _permissionService, TestContext.Current.CancellationToken);

        Assert.Equal($"/api/permissions/{created.Id}", result.Location);
        Assert.Same(created, result.Value);
    }

    [Fact]
    public async Task UpdatePermissionAsync_ServiceUpdatesPermission_ReturnsOkWithPayload()
    {
        var id = Guid.NewGuid();
        var request = new UpdatePermissionActionDto
        {
            Name = "renamed-permission",
            Description = "An updated description."
        };
        var updated = CreateDto("renamed-permission");
        _permissionService.UpdatePermissionAsync(id, request, Arg.Any<CancellationToken>()).Returns(updated);

        var result = await PermissionEndpoints.UpdatePermissionAsync(id, request, _permissionService, TestContext.Current.CancellationToken);

        Assert.Same(updated, result.Value);
    }

    [Fact]
    public async Task DeactivatePermissionAsync_ServiceDeactivatesPermission_ReturnsNoContent()
    {
        var id = Guid.NewGuid();

        await PermissionEndpoints.DeactivatePermissionAsync(id, _permissionService, TestContext.Current.CancellationToken);

        await _permissionService.Received(1).DeactivatePermissionAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantPermissionAsync_ServiceGrantsPermission_ReturnsNoContent()
    {
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();

        await PermissionEndpoints.GrantPermissionAsync(userId, permissionId, _permissionService, TestContext.Current.CancellationToken);

        await _permissionService.Received(1).GrantPermissionAsync(userId, permissionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokePermissionAsync_ServiceRevokesPermission_ReturnsNoContent()
    {
        var userId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();

        await PermissionEndpoints.RevokePermissionAsync(userId, permissionId, _permissionService, TestContext.Current.CancellationToken);

        await _permissionService.Received(1).RevokePermissionAsync(userId, permissionId, Arg.Any<CancellationToken>());
    }
}
