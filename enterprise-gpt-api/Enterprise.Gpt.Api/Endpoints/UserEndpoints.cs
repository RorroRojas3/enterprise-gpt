using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Endpoints
{
    /// <summary>
    /// Minimal API endpoints for user management. Replaces the former <c>UsersController</c>.
    /// Only <c>me</c> — the self-provisioning call the UI makes on load — is open to every
    /// authenticated user; listing, pre-creation, updates, and deactivation are gated by
    /// <see cref="PermissionEndpointFilter"/> on the built-in Administrator permission. Migrating
    /// off MVC is what makes that gating possible at all: endpoint filters cannot be applied to a
    /// controller, which previously left deactivation open to any authenticated caller.
    /// </summary>
    public static class UserEndpoints
    {
        /// <summary>
        /// Maps the <c>api/users</c> endpoint group.
        /// </summary>
        /// <param name="app">The route builder to map the group onto.</param>
        /// <returns>The same <paramref name="app"/> for chaining.</returns>
        public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("api/users")
                .RequireAuthorization()
                .WithTags("Users")
                // Every route in the group is authorized, so the challenge applies uniformly.
                .ProducesProblem(StatusCodes.Status401Unauthorized);

            group.MapPost("me", GetOrCreateCurrentUserAsync)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            // The route's first declared 400, added with ?permissionId=, ?sort= and ?dir=: paging
            // arguments are clamped rather than rejected, but a filter or an order nobody recognises
            // has to be refused — a page in the wrong order looks exactly like a page in the right one.
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
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapPut("{id:guid}", UpdateUserAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapDelete("{id:guid}", DeactivateUserAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);

            return app;
        }

        // The only handler in the API returning a result union rather than one concrete
        // TypedResults type: the UI treats first sign-in and every later load identically and
        // reads a UserDto off both, so the status has to vary while the body shape does not.
        internal static async Task<Results<Ok<UserDto>, Created<UserDto>>> GetOrCreateCurrentUserAsync(
            IUserService userService, CancellationToken cancellationToken)
        {
            var (user, created) = await userService.GetOrCreateCurrentUserAsync(cancellationToken);
            return created
                ? TypedResults.Created($"/api/users/{user.Id}", user)
                : TypedResults.Ok(user);
        }

        // Query parameters follow the service here rather than leading, as elsewhere in the API:
        // they need defaults so the paging arguments stay optional, and C# requires optional
        // parameters last. Without defaults an absent ?skip= would fail binding with a 400.
        internal static async Task<Ok<PaginatedResponseDto<UserDto>>> SearchUsersAsync(
            IUserService userService, string? name = null, int skip = 0, int take = 20,
            Guid? permissionId = null, string? sort = null, string? dir = null,
            CancellationToken cancellationToken = default)
        {
            var response = await userService.SearchUsersAsync(name, skip, take, permissionId, sort, dir, cancellationToken);
            return TypedResults.Ok(response);
        }

        // Not-found paths throw NotFoundException in the service and surface as 404
        // through the exception-handler chain, matching the model endpoints' contract.
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
    }
}
