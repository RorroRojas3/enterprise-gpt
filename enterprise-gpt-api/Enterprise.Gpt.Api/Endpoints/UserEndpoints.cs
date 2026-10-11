using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Dto.Actions.Users;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Dto.Users;
using Enterprise.Gpt.Service.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>
/// User routes; only <c>me</c>, the self-provisioning call the UI makes on load, is open to every authenticated
/// user.
/// </summary>
public static class UserEndpoints
{
    #region Public static methods

    /// <summary>Maps the <c>api/users</c> endpoint group.</summary>
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/users")
            .RequireAuthorization()
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("me", GetOrCreateCurrentUserAsync)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        // Paging is clamped, but an unknown filter or order is refused: a page in the wrong order looks exactly
        // like one in the right order.
        group.MapGet("", SearchUsersAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapGet("{id:guid}", GetUserAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", CreateUserAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .AddEndpointFilter<ValidationEndpointFilter<CreateUserActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("{id:guid}", UpdateUserAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .AddEndpointFilter<ValidationEndpointFilter<UpdateUserActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("{id:guid}", DeactivateUserAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    // 201 on first sign-in, 200 after; the UI reads the same UserDto off both.
    internal static async Task<Results<Ok<UserDto>, Created<UserDto>>> GetOrCreateCurrentUserAsync(
        IUserService userService, CancellationToken cancellationToken)
    {
        var (user, created) = await userService.GetOrCreateCurrentUserAsync(cancellationToken);
        return created
            ? TypedResults.Created($"/api/users/{user.Id}", user)
            : TypedResults.Ok(user);
    }

    // Query parameters follow the service, unlike elsewhere in the API: they need defaults, or an absent
    // ?skip= fails binding with a 400.
    internal static async Task<Ok<PaginatedResponseDto<UserDto>>> SearchUsersAsync(
        IUserService userService, string? name = null, int skip = 0, int take = 20,
        Guid? permissionId = null, string? sort = null, string? dir = null,
        CancellationToken cancellationToken = default)
    {
        var response = await userService.SearchUsersAsync(name, skip, take, permissionId, sort, dir, cancellationToken);
        return TypedResults.Ok(response);
    }

    internal static async Task<Ok<UserDto>> GetUserAsync(
        Guid id, IUserService userService, CancellationToken cancellationToken)
    {
        var response = await userService.GetUserAsync(id, cancellationToken);
        return TypedResults.Ok(response);
    }

    internal static async Task<Created<UserDto>> CreateUserAsync(
        CreateUserActionDto request, IUserService userService, CancellationToken cancellationToken)
    {
        var response = await userService.CreateUserAsync(request, cancellationToken);
        return TypedResults.Created($"/api/users/{response.Id}", response);
    }

    internal static async Task<Ok<UserDto>> UpdateUserAsync(
        Guid id, UpdateUserActionDto request, IUserService userService, CancellationToken cancellationToken)
    {
        var response = await userService.UpdateUserAsync(id, request, cancellationToken);
        return TypedResults.Ok(response);
    }

    internal static async Task<NoContent> DeactivateUserAsync(
        Guid id, IUserService userService, CancellationToken cancellationToken)
    {
        await userService.DeactivateUserAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    #endregion
}
