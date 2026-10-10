using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Project;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Mappers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Mappers;

public class ProjectMapperTests
{
    private static Project CreateProject(
        string? description = "A test project.", string? instructions = "Be concise.", bool isFavorite = true)
    {
        return new Project
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Name = "Research",
            Description = description,
            Instructions = instructions,
            IsFavorite = isFavorite,
            DateCreated = DateTimeOffset.UtcNow.AddDays(-1),
            DateModified = DateTimeOffset.UtcNow
        };
    }

    private static void AssertMatchesEntity(Project expected, ProjectDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Instructions, actual.Instructions);
        Assert.Equal(expected.IsFavorite, actual.IsFavorite);
        Assert.Equal(expected.DateCreated, actual.DateCreated);
        Assert.Equal(expected.DateModified, actual.DateModified);
    }

    [Fact]
    public void MapToProjectDto_PopulatedEntity_MapsAllProperties()
    {
        var project = CreateProject();

        var dto = project.MapToProjectDto();

        AssertMatchesEntity(project, dto);
    }

    [Fact]
    public void MapToProjectDto_OptionalFieldsUnset_MapsThemAsNull()
    {
        var project = CreateProject(description: null, instructions: null);

        var dto = project.MapToProjectDto();

        Assert.Null(dto.Description);
        Assert.Null(dto.Instructions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MapToProjectDto_EitherFavoriteState_CarriesItThrough(bool isFavorite)
    {
        // Both states, or a mapper assigning the literal true would pass every other test here.
        var project = CreateProject(isFavorite: isFavorite);

        Assert.Equal(isFavorite, project.MapToProjectDto().IsFavorite);
        Assert.Equal(isFavorite, ProjectMapper.MapToProjectDtoExpression.Compile()(project).IsFavorite);
        Assert.Equal(isFavorite, ProjectMapper.MapToProjectSummaryDtoExpression.Compile()(project).IsFavorite);
    }

    [Fact]
    public void MapToProjectDtoExpression_CompiledDelegate_MatchesMapToProjectDto()
    {
        // The two are maintained separately — one for materialized entities, one for server-side
        // projection — so they have to be asserted against each other or they drift.
        var project = CreateProject();

        var projected = ProjectMapper.MapToProjectDtoExpression.Compile()(project);

        AssertMatchesEntity(project, projected);
    }

    [Fact]
    public void MapToProjectSummaryDtoExpression_CompiledDelegate_CarriesEverythingButInstructions()
    {
        // The whole reason the summary projection exists: a listing must not ship the instruction
        // text, which is the largest field on the type.
        var project = CreateProject(instructions: "Answer in British English.");

        var summary = ProjectMapper.MapToProjectSummaryDtoExpression.Compile()(project);

        Assert.Equal(project.Id, summary.Id);
        Assert.Equal(project.Name, summary.Name);
        Assert.Equal(project.Description, summary.Description);
        Assert.Equal(project.IsFavorite, summary.IsFavorite);
        Assert.Equal(project.DateCreated, summary.DateCreated);
        Assert.Equal(project.DateModified, summary.DateModified);
        Assert.IsNotType<ProjectDto>(summary);
    }

    [Fact]
    public void FromCreateProjectActionDtoToProject_Request_CopiesTheEditableFieldsOnly()
    {
        var request = new CreateProjectActionDto
        {
            Name = "Research",
            Description = "A test project.",
            Instructions = "Be concise."
        };

        var project = request.FromCreateProjectActionDtoToProject();

        Assert.Equal(request.Name, project.Name);
        Assert.Equal(request.Description, project.Description);
        Assert.Equal(request.Instructions, project.Instructions);
        // Identity and ownership are the caller's responsibility, not the mapper's.
        Assert.Equal(Guid.Empty, project.Id);
        Assert.Equal(Guid.Empty, project.UserId);
    }

    [Fact]
    public void FromUpdateProjectActionDtoToProject_Request_OverwritesTheEditableFieldsInPlace()
    {
        var project = CreateProject();
        var originalId = project.Id;
        var originalUserId = project.UserId;
        var request = new UpdateProjectActionDto
        {
            Name = "Renamed",
            Description = "Updated description.",
            Instructions = null
        };

        request.FromUpdateProjectActionDtoToProject(project);

        Assert.Equal("Renamed", project.Name);
        Assert.Equal("Updated description.", project.Description);
        Assert.Null(project.Instructions);
        Assert.Equal(originalId, project.Id);
        Assert.Equal(originalUserId, project.UserId);
        // Not an editable field of this DTO: the update is a full representation, so writing the
        // flag here would clear the star on every rename.
        Assert.True(project.IsFavorite);
    }
}
