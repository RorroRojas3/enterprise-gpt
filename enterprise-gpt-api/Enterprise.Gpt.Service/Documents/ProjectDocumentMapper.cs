using System.Linq.Expressions;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Entity.Documents;

namespace Enterprise.Gpt.Service.Documents;

/// <summary>
/// Maps <see cref="ProjectDocument"/> to its DTO, in both the materialized and the
/// server-side-projection forms.
/// </summary>
public static class ProjectDocumentMapper
{
    /// <summary>Server-side projection; omits chunk data so listings never load embeddings.</summary>
    public static Expression<Func<ProjectDocument, ProjectDocumentDto>> MapToProjectDocumentDtoExpression { get; } =
        document => new ProjectDocumentDto
        {
            Id = document.Id,
            ProjectId = document.ProjectId,
            Name = document.Name,
            Extension = document.Extension,
            MimeType = document.MimeType,
            Size = document.Size,
            DateCreated = document.DateCreated
        };

    #region Public static methods

    public static ProjectDocumentDto MapToProjectDocumentDto(this ProjectDocument document)
    {
        return new ProjectDocumentDto
        {
            Id = document.Id,
            ProjectId = document.ProjectId,
            Name = document.Name,
            Extension = document.Extension,
            MimeType = document.MimeType,
            Size = document.Size,
            DateCreated = document.DateCreated
        };
    }

    #endregion
}
