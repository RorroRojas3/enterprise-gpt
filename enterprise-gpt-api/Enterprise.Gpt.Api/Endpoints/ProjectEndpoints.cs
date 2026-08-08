using Microsoft.AspNetCore.Http.HttpResults;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Project;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for projects: the container that groups a user's conversations and owns
/// the document set and standing instructions shared across them.
/// </summary>
/// <remarks>
/// Every route is scoped to the signed-in user rather than gated on a permission — a project is
/// owned the way a conversation is, and there is no administrative view of another user's
/// projects. Uploading into a project lives on <see cref="DocumentEndpoints"/> instead, so a
/// single upload-status route serves conversation and project uploads alike.
/// </remarks>
public static class ProjectEndpoints
{
    /// <summary>
    /// Maps the <c>api/projects</c> endpoint group.
    /// </summary>
    /// <param name="app">The route builder to map the group onto.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/projects")
            .RequireAuthorization()
            .WithTags("Projects")
            // Every route in the group is authorized, so the challenge applies uniformly.
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("", SearchProjectsAsync);
        group.MapGet("{id:guid}", GetProjectAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", CreateProjectAsync)
            .ProducesValidationProblem();
        group.MapPut("{id:guid}", UpdateProjectAsync)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("{id:guid}", DeactivateProjectAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("{id:guid}/documents", GetProjectDocumentsAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("{projectId:guid}/documents/{documentId:guid}", DeactivateProjectDocumentAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    internal static async Task<Ok<PaginatedResponseDto<ProjectSummaryDto>>> SearchProjectsAsync(
        IProjectService projectService, string? name = null, int skip = 0, int take = 20,
        CancellationToken cancellationToken = default)
    {
        var response = await projectService.SearchProjectsAsync(name, skip, take, cancellationToken);
        return TypedResults.Ok(response);
    }

    // Not-found paths throw NotFoundException in the service and surface as 404 through the
    // exception-handler chain. A project belonging to another user is reported the same way, so
    // this API cannot be used to probe for project ids.
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
}
