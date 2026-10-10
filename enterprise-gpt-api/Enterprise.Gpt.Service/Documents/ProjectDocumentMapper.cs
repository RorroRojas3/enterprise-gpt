using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Mappers;

/// <summary>
/// Maps <see cref="ProjectDocument"/> to its DTO, in both the materialized and the
/// server-side-projection forms.
/// </summary>
public static class ProjectDocumentMapper
{
    /// <summary>
    /// Maps a materialized <see cref="ProjectDocument"/> entity to <see cref="ProjectDocumentDto"/>.
    /// </summary>
    /// <param name="document">The loaded document entity.</param>
    /// <returns>The mapped DTO.</returns>
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

    /// <summary>
    /// LINQ projection from <see cref="ProjectDocument"/> to <see cref="ProjectDocumentDto"/> for use
    /// inside <c>IQueryable.Select(...)</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately projects no chunk data: a document listing must never drag the embedding column
    /// across the wire.
    /// </remarks>
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
}
