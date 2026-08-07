using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Permission;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Endpoints
{
    /// <summary>
    /// Minimal API endpoints for permission management and user grants. Every route is gated
    /// on the built-in Administrator permission: permissions are an administrative concern, and
    /// regular users receive theirs through the user profile endpoints. Built-in and
    /// MCP-managed permissions reject mutations in the service layer.
    /// </summary>
    public static class PermissionEndpoints
    {
        /// <summary>
        /// Maps the <c>api/permissions</c> group and the <c>api/users/{userId}/permissions</c>
        /// grant routes.
        /// </summary>
        /// <param name="app">The route builder to map the groups onto.</param>
        /// <returns>The same <paramref name="app"/> for chaining.</returns>
        public static IEndpointRouteBuilder MapPermissionEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("api/permissions")
                .RequireAuthorization()
                .WithTags("Permissions")
                // Every route in the group is authorized, so the challenge applies uniformly.
                .ProducesProblem(StatusCodes.Status401Unauthorized);

            group.MapGet("", GetPermissionsAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden);
            group.MapGet("all", GetAllPermissionsAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden);
            group.MapGet("{id:guid}", GetPermissionAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapPost("", CreatePermissionAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden);
            group.MapPut("{id:guid}", UpdatePermissionAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapDelete("{id:guid}", DeactivatePermissionAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);

            var grants = app.MapGroup("api/users/{userId:guid}/permissions")
                .RequireAuthorization()
                .WithTags("Permissions")
                // Every route in the group is authorized, so the challenge applies uniformly.
                .ProducesProblem(StatusCodes.Status401Unauthorized);

            grants.MapPost("{permissionId:guid}", GrantPermissionAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            grants.MapDelete("{permissionId:guid}", RevokePermissionAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);

            return app;
        }

        internal static async Task<Ok<List<PermissionDto>>> GetPermissionsAsync(
            IPermissionService permissionService, CancellationToken cancellationToken)
        {
            var response = await permissionService.GetPermissionsAsync(cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<Ok<List<PermissionDto>>> GetAllPermissionsAsync(
            IPermissionService permissionService, CancellationToken cancellationToken)
        {
            var response = await permissionService.GetAllPermissionsAsync(cancellationToken);
            return TypedResults.Ok(response);
        }

        // Not-found paths throw NotFoundException in the service and surface as 404
        // through the exception-handler chain, matching the model endpoints' contract.
        internal static async Task<Ok<PermissionDto>> GetPermissionAsync(
            Guid id, IPermissionService permissionService, CancellationToken cancellationToken)
        {
            var response = await permissionService.GetPermissionAsync(id, cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<Created<PermissionDto>> CreatePermissionAsync(
            CreatePermissionActionDto request, IPermissionService permissionService, CancellationToken cancellationToken)
        {
            var response = await permissionService.CreatePermissionAsync(request, cancellationToken);
            return TypedResults.Created($"/api/permissions/{response.Id}", response);
        }

        internal static async Task<Ok<PermissionDto>> UpdatePermissionAsync(
            Guid id, UpdatePermissionActionDto request, IPermissionService permissionService, CancellationToken cancellationToken)
        {
            var response = await permissionService.UpdatePermissionAsync(id, request, cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<NoContent> DeactivatePermissionAsync(
            Guid id, IPermissionService permissionService, CancellationToken cancellationToken)
        {
            await permissionService.DeactivatePermissionAsync(id, cancellationToken);
            return TypedResults.NoContent();
        }

        internal static async Task<NoContent> GrantPermissionAsync(
            Guid userId, Guid permissionId, IPermissionService permissionService, CancellationToken cancellationToken)
        {
            await permissionService.GrantPermissionAsync(userId, permissionId, cancellationToken);
            return TypedResults.NoContent();
        }

        internal static async Task<NoContent> RevokePermissionAsync(
            Guid userId, Guid permissionId, IPermissionService permissionService, CancellationToken cancellationToken)
        {
            await permissionService.RevokePermissionAsync(userId, permissionId, cancellationToken);
            return TypedResults.NoContent();
        }
    }
}
