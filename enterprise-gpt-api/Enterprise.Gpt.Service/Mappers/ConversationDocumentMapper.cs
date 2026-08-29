using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Mappers;

/// <summary>
/// Maps <see cref="ConversationDocument"/> to its DTOs, in both the materialized and the
/// server-side-projection forms.
/// </summary>
public static class ConversationDocumentMapper
{
    /// <summary>
    /// Maps a materialized <see cref="ConversationDocument"/> entity to
    /// <see cref="ConversationDocumentDto"/>.
    /// </summary>
    /// <param name="document">The loaded document entity.</param>
    /// <returns>The mapped DTO.</returns>
    public static ConversationDocumentDto MapToConversationDocumentDto(this ConversationDocument document)
    {
        return new ConversationDocumentDto
        {
            Id = document.Id,
            ConversationId = document.ConversationId,
            Name = document.Name,
            Extension = document.Extension,
            MimeType = document.MimeType,
            Size = document.Size,
            Type = document.Type,
            DateCreated = document.DateCreated
        };
    }

    /// <summary>
    /// LINQ projection from <see cref="ConversationDocument"/> to
    /// <see cref="ConversationDocumentDto"/> for use inside <c>IQueryable.Select(...)</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately projects no chunk data: a document listing must never drag the embedding column
    /// across the wire.
    /// </remarks>
    public static Expression<Func<ConversationDocument, ConversationDocumentDto>> MapToConversationDocumentDtoExpression { get; } =
        document => new ConversationDocumentDto
        {
            Id = document.Id,
            ConversationId = document.ConversationId,
            Name = document.Name,
            Extension = document.Extension,
            MimeType = document.MimeType,
            Size = document.Size,
            Type = document.Type,
            DateCreated = document.DateCreated
        };

    /// <summary>
    /// LINQ projection from <see cref="ConversationDocument"/> to <see cref="UserDocumentDto"/> for
    /// use inside <c>IQueryable.Select(...)</c>, joining in the owning conversation's name for the
    /// cross-conversation documents library.
    /// </summary>
    /// <remarks>
    /// Deliberately projects no chunk data: a document listing must never drag the embedding column
    /// across the wire.
    /// </remarks>
    public static Expression<Func<ConversationDocument, UserDocumentDto>> MapToUserDocumentDtoExpression { get; } =
        document => new UserDocumentDto
        {
            Id = document.Id,
            ConversationId = document.ConversationId,
            ConversationName = document.Conversation.Name,
            Name = document.Name,
            Extension = document.Extension,
            MimeType = document.MimeType,
            Size = document.Size,
            Type = document.Type,
            DateCreated = document.DateCreated
        };
}
