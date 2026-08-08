using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Project;
using Enterprise.Gpt.Service;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class ProjectEndpointsTests
{
    private readonly IProjectService _projectService = Substitute.For<IProjectService>();

    private static ProjectDto CreateDto(string name = "Research")
    {
        return new ProjectDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            Instructions = "Be concise.",
            DateCreated = DateTimeOffset.UtcNow.AddDays(-1),
            DateModified = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public async Task SearchProjectsAsync_ServiceReturnsAPage_ReturnsOkWithPayload()
    {
        var expected = new PaginatedResponseDto<ProjectSummaryDto>
        {
            Items = [new ProjectSummaryDto { Id = Guid.NewGuid(), Name = "Research" }],
            TotalCount = 1,
            PageSize = 20,
            CurrentPage = 1
        };
        _projectService.SearchProjectsAsync("res", 0, 20, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ProjectEndpoints.SearchProjectsAsync(
            _projectService, "res", 0, 20, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task SearchProjectsAsync_NoQueryString_UsesTheDefaultPage()
    {
        _projectService.SearchProjectsAsync(null, 0, 20, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<ProjectSummaryDto>());

        await ProjectEndpoints.SearchProjectsAsync(_projectService, cancellationToken: TestContext.Current.CancellationToken);

        await _projectService.Received(1).SearchProjectsAsync(null, 0, 20, Arg.Any<CancellationToken>());
    }

    // Not-found paths throw NotFoundException in the service and surface as 404 through the
    // exception-handler chain, so the handlers themselves only have the success shape to assert.
    [Fact]
    public async Task GetProjectAsync_ServiceReturnsProject_ReturnsOkWithPayload()
    {
        var expected = CreateDto();
        _projectService.GetProjectAsync(expected.Id, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ProjectEndpoints.GetProjectAsync(expected.Id, _projectService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task CreateProjectAsync_ServiceCreatesProject_ReturnsCreatedWithLocation()
    {
        var request = new CreateProjectActionDto { Name = "Research", Description = "A project.", Instructions = null };
        var created = CreateDto();
        _projectService.CreateProjectAsync(request, Arg.Any<CancellationToken>()).Returns(created);

        var result = await ProjectEndpoints.CreateProjectAsync(request, _projectService, TestContext.Current.CancellationToken);

        Assert.Same(created, result.Value);
        Assert.Equal($"/api/projects/{created.Id}", result.Location);
    }

    [Fact]
    public async Task UpdateProjectAsync_ServiceUpdatesProject_ReturnsOkWithPayload()
    {
        var id = Guid.NewGuid();
        var request = new UpdateProjectActionDto { Name = "Renamed", Description = null, Instructions = null };
        var updated = CreateDto("Renamed");
        _projectService.UpdateProjectAsync(id, request, Arg.Any<CancellationToken>()).Returns(updated);

        var result = await ProjectEndpoints.UpdateProjectAsync(id, request, _projectService, TestContext.Current.CancellationToken);

        Assert.Same(updated, result.Value);
    }

    [Fact]
    public async Task DeactivateProjectAsync_ServiceSucceeds_ReturnsNoContent()
    {
        var id = Guid.NewGuid();

        var result = await ProjectEndpoints.DeactivateProjectAsync(id, _projectService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _projectService.Received(1).DeactivateProjectAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetProjectDocumentsAsync_ServiceReturnsDocuments_ReturnsOkWithPayload()
    {
        var projectId = Guid.NewGuid();
        List<ProjectDocumentDto> expected = [new() { Id = Guid.NewGuid(), ProjectId = projectId, Name = "spec.pdf", Extension = ".pdf", MimeType = "application/pdf", Size = 10 }];
        _projectService.GetProjectDocumentsAsync(projectId, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ProjectEndpoints.GetProjectDocumentsAsync(projectId, _projectService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task DeactivateProjectDocumentAsync_ServiceSucceeds_ReturnsNoContent()
    {
        var projectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var result = await ProjectEndpoints.DeactivateProjectDocumentAsync(
            projectId, documentId, _projectService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _projectService.Received(1).DeactivateProjectDocumentAsync(projectId, documentId, Arg.Any<CancellationToken>());
    }
}
