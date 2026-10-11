using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Dto.Actions.Projects;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Dto.Projects;
using Enterprise.Gpt.Service.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>
/// Project routes; a project groups a user's conversations and owns the documents and instructions they share.
/// </summary>
/// <remarks>
/// Scoped to the signed-in user rather than gated on a permission: there is no administrative view of another
/// user's projects.
/// </remarks>
public static class ProjectEndpoints
{
    #region Public static methods

    /// <summary>Maps the <c>api/projects</c> endpoint group.</summary>
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/projects")
            .RequireAuthorization()
            .WithTags("Projects")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // A bad ?sort= or ?dir= is a validation problem and a binding failure a bare 400; OpenAPI allows one
        // schema per status, so the richer is declared.
        group.MapGet("", SearchProjectsAsync)
            .ProducesValidationProblem();
        group.MapGet("{id:guid}", GetProjectAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", CreateProjectAsync)
            .AddEndpointFilter<ValidationEndpointFilter<CreateProjectActionDto>>()
            .ProducesValidationProblem();
        group.MapPut("{id:guid}", UpdateProjectAsync)
            .AddEndpointFilter<ValidationEndpointFilter<UpdateProjectActionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("{id:guid}", DeactivateProjectAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // No validator, so the only 400 is a binding failure on the bool.
        group.MapPut("{id:guid}/favorite", SetProjectFavoriteAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("{id:guid}/documents", GetProjectDocumentsAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("{projectId:guid}/documents/{documentId:guid}", DeactivateProjectDocumentAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    // Query parameters follow the service, unlike elsewhere in the API, because they need defaults.
    internal static async Task<Ok<PaginatedResponseDto<ProjectSummaryDto>>> SearchProjectsAsync(
        IProjectService projectService, string? name = null, int skip = 0, int take = 20,
        bool? isFavorite = null, string? sort = null, string? dir = null,
        CancellationToken cancellationToken = default)
    {
        var response = await projectService.SearchProjectsAsync(name, skip, take, isFavorite, sort, dir, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Another user's project is a 404 too, so ids cannot be probed.
    internal static async Task<Ok<ProjectDto>> GetProjectAsync(
        Guid id, IProjectService projectService, CancellationToken cancellationToken)
    {
        var response = await projectService.GetProjectAsync(id, cancellationToken);
        return TypedResults.Ok(response);
    }

    internal static async Task<Created<ProjectDto>> CreateProjectAsync(
        CreateProjectActionDto request, IProjectService projectService, CancellationToken cancellationToken)
    {
        var response = await projectService.CreateProjectAsync(request, cancellationToken);
        return TypedResults.Created($"/api/projects/{response.Id}", response);
    }

    internal static async Task<Ok<ProjectDto>> UpdateProjectAsync(
        Guid id, UpdateProjectActionDto request, IProjectService projectService, CancellationToken cancellationToken)
    {
        var response = await projectService.UpdateProjectAsync(id, request, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Its own route rather than a field on the PUT above: that PUT is a full representation, so a
    // rename that forgot to echo the flag back would silently un-favourite the project.
    internal static async Task<NoContent> SetProjectFavoriteAsync(
        Guid id, SetProjectFavoriteActionDto request, IProjectService projectService,
        CancellationToken cancellationToken)
    {
        await projectService.SetProjectFavoriteAsync(id, request, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<NoContent> DeactivateProjectAsync(
        Guid id, IProjectService projectService, CancellationToken cancellationToken)
    {
        await projectService.DeactivateProjectAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Ok<List<ProjectDocumentDto>>> GetProjectDocumentsAsync(
        Guid id, IProjectService projectService, CancellationToken cancellationToken)
    {
        var response = await projectService.GetProjectDocumentsAsync(id, cancellationToken);
        return TypedResults.Ok(response);
    }

    internal static async Task<NoContent> DeactivateProjectDocumentAsync(
        Guid projectId, Guid documentId, IProjectService projectService, CancellationToken cancellationToken)
    {
        await projectService.DeactivateProjectDocumentAsync(projectId, documentId, cancellationToken);
        return TypedResults.NoContent();
    }

    #endregion
}
