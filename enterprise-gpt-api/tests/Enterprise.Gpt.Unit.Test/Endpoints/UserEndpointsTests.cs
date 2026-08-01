using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Service;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class UserEndpointsTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();

    private static UserDto CreateDto(string firstName = "Ada")
    {
        return new UserDto
        {
            Id = Guid.NewGuid(),
            FirstName = firstName,
            LastName = "Lovelace",
            Email = $"{firstName.ToLowerInvariant()}.lovelace@example.com",
            Permissions = []
        };
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_UserAlreadyExists_ReturnsOkWithPayload()
    {
        var expected = CreateDto();
        _userService.GetOrCreateCurrentUserAsync(Arg.Any<CancellationToken>()).Returns((expected, false));

        var result = await UserEndpoints.GetOrCreateCurrentUserAsync(_userService, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<UserDto>>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task GetOrCreateCurrentUserAsync_UserProvisioned_ReturnsCreatedWithLocation()
    {
        var expected = CreateDto();
        _userService.GetOrCreateCurrentUserAsync(Arg.Any<CancellationToken>()).Returns((expected, true));

        var result = await UserEndpoints.GetOrCreateCurrentUserAsync(_userService, TestContext.Current.CancellationToken);

        var created = Assert.IsType<Created<UserDto>>(result.Result);
        Assert.Equal($"/api/users/{expected.Id}", created.Location);
        Assert.Same(expected, created.Value);
    }

    [Fact]
    public async Task SearchUsersAsync_ServiceReturnsPage_ReturnsOkWithPayload()
    {
        var expected = new PaginatedResponseDto<UserDto>
        {
            Items = [CreateDto()],
            TotalCount = 1,
            PageSize = 20,
            CurrentPage = 1
        };
        _userService.SearchUsersAsync("ada", 0, 20, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await UserEndpoints.SearchUsersAsync(_userService, "ada", 0, 20, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task SearchUsersAsync_NoQueryArguments_PassesDefaultPagingToService()
    {
        var expected = new PaginatedResponseDto<UserDto>();
        _userService.SearchUsersAsync(null, 0, 20, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await UserEndpoints.SearchUsersAsync(
            _userService, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
        await _userService.Received(1).SearchUsersAsync(null, 0, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserAsync_ServiceReturnsUser_ReturnsOkWithPayload()
    {
        var expected = CreateDto();
        _userService.GetUserAsync(expected.Id, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await UserEndpoints.GetUserAsync(expected.Id, _userService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task CreateUserAsync_ServiceCreatesUser_ReturnsCreatedWithLocation()
    {
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [Guid.NewGuid()]
        };
        var expected = CreateDto("Grace");
        _userService.CreateUserAsync(request, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await UserEndpoints.CreateUserAsync(request, _userService, TestContext.Current.CancellationToken);

        Assert.Equal($"/api/users/{expected.Id}", result.Location);
        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task UpdateUserAsync_ServiceUpdatesUser_ReturnsOkWithPayload()
    {
        var request = new UpdateUserActionDto
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace.hopper@example.com",
            PermissionIds = [Guid.NewGuid()]
        };
        var expected = CreateDto("Grace");
        _userService.UpdateUserAsync(expected.Id, request, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await UserEndpoints.UpdateUserAsync(expected.Id, request, _userService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task DeactivateUserAsync_ServiceDeactivatesUser_ReturnsNoContent()
    {
        var id = Guid.NewGuid();

        var result = await UserEndpoints.DeactivateUserAsync(id, _userService, TestContext.Current.CancellationToken);

        Assert.IsType<NoContent>(result);
        await _userService.Received(1).DeactivateUserAsync(id, Arg.Any<CancellationToken>());
    }
}
