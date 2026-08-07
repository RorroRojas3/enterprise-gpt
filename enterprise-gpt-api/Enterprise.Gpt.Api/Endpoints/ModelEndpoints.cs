using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Endpoints
{
    /// <summary>
    /// Minimal API endpoints for model management. Replaces the former <c>ModelsController</c>.
    /// Read endpoints are available to every authenticated user; mutations and the
    /// active+inactive listing are gated on the built-in Administrator permission via
    /// <see cref="PermissionEndpointFilter"/>.
    /// </summary>
    public static class ModelEndpoints
    {
        /// <summary>
        /// Maps the <c>api/models</c> endpoint group.
        /// </summary>
        /// <param name="app">The route builder to map the group onto.</param>
        /// <returns>The same <paramref name="app"/> for chaining.</returns>
        public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("api/models")
                .RequireAuthorization()
                .WithTags("Models")
                // Every route in the group is authorized, so the challenge applies uniformly.
                .ProducesProblem(StatusCodes.Status401Unauthorized);

            group.MapGet("", GetModelsAsync);
            group.MapGet("all", GetAllModelsAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden);
            group.MapGet("{id:guid}", GetModelAsync)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapPost("", CreateModelAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapPut("{id:guid}", UpdateModelAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
            group.MapDelete("{id:guid}", DeactivateModelAsync)
                .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);

            return app;
        }

        internal static async Task<Ok<List<ModelDto>>> GetModelsAsync(
            IModelService modelService, CancellationToken cancellationToken)
        {
            var response = await modelService.GetModelsAsync(cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<Ok<List<ModelDto>>> GetAllModelsAsync(
            IModelService modelService, CancellationToken cancellationToken)
        {
            var response = await modelService.GetAllModelsAsync(cancellationToken);
            return TypedResults.Ok(response);
        }

        // Not-found paths throw NotFoundException in the service and surface as 404
        // through the exception-handler chain, matching the controller-era contract.
        internal static async Task<Ok<ModelDto>> GetModelAsync(
            Guid id, IModelService modelService, CancellationToken cancellationToken)
        {
            var response = await modelService.GetModelAsync(id, cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<Created<ModelDto>> CreateModelAsync(
            CreateModelActionDto request, IModelService modelService, CancellationToken cancellationToken)
        {
            var response = await modelService.CreateModelAsync(request, cancellationToken);
            return TypedResults.Created($"/api/models/{response.Id}", response);
        }

        internal static async Task<Ok<ModelDto>> UpdateModelAsync(
            Guid id, UpdateModelActionDto request, IModelService modelService, CancellationToken cancellationToken)
        {
            var response = await modelService.UpdateModelAsync(id, request, cancellationToken);
            return TypedResults.Ok(response);
        }

        internal static async Task<NoContent> DeactivateModelAsync(
            Guid id, IModelService modelService, CancellationToken cancellationToken)
        {
            await modelService.DeactivateModelAsync(id, cancellationToken);
            return TypedResults.NoContent();
        }
    }
}
