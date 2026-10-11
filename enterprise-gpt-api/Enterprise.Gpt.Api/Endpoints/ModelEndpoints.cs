using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Dto.Actions.ChatModels;
using Enterprise.Gpt.Dto.ChatModels;
using Enterprise.Gpt.Service.ChatModels;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>
/// Model catalog routes: reads for any authenticated user, mutations and the full listing for admins.
/// </summary>
public static class ModelEndpoints
{
    #region Public static methods

    /// <summary>Maps the <c>api/models</c> endpoint group.</summary>
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/models")
            .RequireAuthorization()
            .WithTags("Models")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("", GetModelsAsync);
        group.MapGet("all", GetAllModelsAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapGet("{id:guid}", GetModelAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", CreateModelAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .AddEndpointFilter<ValidationEndpointFilter<CreateModelActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("{id:guid}", UpdateModelAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .AddEndpointFilter<ValidationEndpointFilter<UpdateModelActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("{id:guid}", DeactivateModelAsync)
            .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            // The configured document summarizer is refused: the application will not start
            // without its catalog row.
            .ProducesProblem(StatusCodes.Status409Conflict);

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

    #endregion
}
