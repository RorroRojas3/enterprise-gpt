using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Endpoints
{
    /// <summary>
    /// Minimal API endpoints for user management. Replaces the former <c>UsersController</c>.
    /// Only <c>me</c> — the self-provisioning call the UI makes on load — is open to every
    /// authenticated user; listing, pre-creation, updates, and deactivation are gated by
    /// <see cref="AdminEndpointFilter"/>. Migrating off MVC is what makes that gating possible at
    /// all: the filter is an <see cref="IEndpointFilter"/> and cannot be applied to a controller,
    /// which previously left deactivation open to any authenticated caller.
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
                .WithTags("Users");

            group.MapPost("me", GetOrCreateCurrentUserAsync)
                .Produces<ErrorDto>(StatusCodes.Status403Forbidden)
                .Produces<ErrorDto>(StatusCodes.Status404NotFound);
            group.MapGet("", SearchUsersAsync)
                .AddEndpointFilter<AdminEndpointFilter>()
                .Produces<ErrorDto>(StatusCodes.Status403Forbidden);
            group.MapGet("{id:guid}", GetUserAsync)
                .AddEndpointFilter<AdminEndpointFilter>()
                .Produces<ErrorDto>(StatusCodes.Status403Forbidden)
                .Produces<ErrorDto>(StatusCodes.Status404NotFound);
            group.MapPost("", CreateUserAsync)
                .AddEndpointFilter<AdminEndpointFilter>()
                .Produces<ErrorDto>(StatusCodes.Status400BadRequest)
                .Produces<ErrorDto>(StatusCodes.Status403Forbidden)
                .Produces<ErrorDto>(StatusCodes.Status404NotFound);
            group.MapPut("{id:guid}", UpdateUserAsync)
                .AddEndpointFilter<AdminEndpointFilter>()
                .Produces<ErrorDto>(StatusCodes.Status400BadRequest)
                .Produces<ErrorDto>(StatusCodes.Status403Forbidden)
                .Produces<ErrorDto>(StatusCodes.Status404NotFound);
            group.MapDelete("{id:guid}", DeactivateUserAsync)
                .AddEndpointFilter<AdminEndpointFilter>()
                .Produces<ErrorDto>(StatusCodes.Status400BadRequest)
                .Produces<ErrorDto>(StatusCodes.Status403Forbidden)
                .Produces<ErrorDto>(StatusCodes.Status404NotFound);

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
            IUserService userService, string? name = null, int skip = 0, int take = 20, CancellationToken cancellationToken = default)
        {
            var response = await userService.SearchUsersAsync(name, skip, take, cancellationToken);
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
