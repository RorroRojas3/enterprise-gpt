using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Endpoints
{
    /// <summary>
    /// Minimal API endpoints for MCP server management. Replaces the former <c>McpsController</c>.
    /// The user-facing listing returns only servers the caller holds a permission for; the
    /// administrative listing and all mutations are gated on the built-in Administrator permission
    /// via <see cref="PermissionEndpointFilter"/>.
    /// </summary>
    public static class McpEndpoints
    {
        /// <summary>
        /// Maps the <c>api/mcps</c> endpoint group.
        /// </summary>
        /// <param name="app">The route builder to map the group onto.</param>
        /// <returns>The same <paramref name="app"/> for chaining.</returns>
        public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("api/mcps")
                .RequireAuthorization()
                .WithTags("Mcps")
                // Every route in the group is authorized, so the challenge applies uniformly.
                .ProducesProblem(StatusCodes.Status401Unauthorized);

            group.MapGet("", GetPermittedMcpServersAsync);
            group.MapGet("all", GetAllMcpServersAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden);
            group.MapGet("{id:guid}", GetMcpServerAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapPost("", CreateMcpServerAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden);
            group.MapPut("{id:guid}", UpdateMcpServerAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapDelete("{id:guid}", DeactivateMcpServerAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);

            return app;
        }

        internal static async Task<Ok<List<McpDto>>> GetPermittedMcpServersAsync(
            IMcpServerService mcpServerService, CancellationToken cancellationToken)
        {
            var response = await mcpServerService.GetPermittedMcpServersAsync(cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<Ok<List<McpServerDto>>> GetAllMcpServersAsync(
            IMcpServerService mcpServerService, CancellationToken cancellationToken)
        {
            var response = await mcpServerService.GetAllMcpServersAsync(cancellationToken);
            return TypedResults.Ok(response);
        }

        // Not-found paths throw NotFoundException in the service and surface as 404
        // through the exception-handler chain, matching the model endpoints' contract.
        internal static async Task<Ok<McpServerDto>> GetMcpServerAsync(
            Guid id, IMcpServerService mcpServerService, CancellationToken cancellationToken)
        {
            var response = await mcpServerService.GetMcpServerAsync(id, cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<Created<McpServerDto>> CreateMcpServerAsync(
            CreateMcpServerActionDto request, IMcpServerService mcpServerService, CancellationToken cancellationToken)
        {
            var response = await mcpServerService.CreateMcpServerAsync(request, cancellationToken);
            return TypedResults.Created($"/api/mcps/{response.Id}", response);
        }

        internal static async Task<Ok<McpServerDto>> UpdateMcpServerAsync(
            Guid id, UpdateMcpServerActionDto request, IMcpServerService mcpServerService, CancellationToken cancellationToken)
        {
            var response = await mcpServerService.UpdateMcpServerAsync(id, request, cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<NoContent> DeactivateMcpServerAsync(
            Guid id, IMcpServerService mcpServerService, CancellationToken cancellationToken)
        {
            await mcpServerService.DeactivateMcpServerAsync(id, cancellationToken);
            return TypedResults.NoContent();
        }
    }
}
