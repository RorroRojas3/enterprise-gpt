using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Mappers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Mappers;

public class ProjectDocumentMapperTests
{
    private static ProjectDocument CreateDocument()
    {
        return new ProjectDocument
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Name = "spec.pdf",
            Extension = ".pdf",
            MimeType = "application/pdf",
            Size = 2048,
            Path = "user/projects/project/document.pdf",
            DateCreated = DateTimeOffset.UtcNow.AddHours(-1),
            DateModified = DateTimeOffset.UtcNow
        };
    }

    private static void AssertMatchesEntity(ProjectDocument expected, ProjectDocumentDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ProjectId, actual.ProjectId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Extension, actual.Extension);
        Assert.Equal(expected.MimeType, actual.MimeType);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.DateCreated, actual.DateCreated);
    }

    [Fact]
    public void MapToProjectDocumentDto_PopulatedEntity_MapsAllProperties()
    {
        var document = CreateDocument();

        var dto = document.MapToProjectDocumentDto();

        AssertMatchesEntity(document, dto);
        Assert.Equal(document.Id, dto.DocumentId);
    }

    [Fact]
    public void MapToProjectDocumentDtoExpression_CompiledDelegate_MatchesMapToProjectDocumentDto()
    {
        var document = CreateDocument();

        var projected = ProjectDocumentMapper.MapToProjectDocumentDtoExpression.Compile()(document);

        AssertMatchesEntity(document, projected);
    }
}
