using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Microsoft.EntityFrameworkCore;

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
    /// Builds the LINQ projection from <see cref="ConversationDocument"/> to
    /// <see cref="UserDocumentDto"/> for use inside <c>IQueryable.Select(...)</c>, joining in the
    /// owning conversation's name and a count of its documents matching the same filters.
    /// </summary>
    /// <param name="origin">The origin the listing was narrowed to, or <see langword="null"/> for every document.</param>
    /// <param name="name">The document-name substring the listing was narrowed by, or <see langword="null"/> for all.</param>
    /// <returns>The projection, closed over the filters the listing is running.</returns>
    /// <remarks>
    /// A method rather than a property because the count has to re-apply the caller's filters, which
    /// a static expression cannot close over. It deliberately projects no chunk data: a document
    /// listing must never drag the embedding column across the wire.
    /// </remarks>
    public static Expression<Func<ConversationDocument, UserDocumentDto>> MapToUserDocumentDtoExpression(
        ConversationDocumentTypes? origin, string? name)
    {
        // Hoisted so the tree closes over plain values EF parameterizes, rather than over a nullable
        // enum it would translate as a null comparison on every row.
        var hasOrigin = origin.HasValue;
        var originType = origin.GetValueOrDefault();
        var pattern = string.IsNullOrWhiteSpace(name) ? null : $"%{name}%";

        return document => new UserDocumentDto
        {
            Id = document.Id,
            ConversationId = document.ConversationId,
            ConversationName = document.Conversation.Name,
            // The owner needs no predicate here: siblings under one conversation share its owner.
            // Served by the (ConversationId, DateDeactivated) index the per-conversation listing added.
            ConversationDocumentCount = document.Conversation.Documents.Count(sibling =>
                !sibling.DateDeactivated.HasValue
                && (!hasOrigin || sibling.Type == originType)
                && (pattern == null || EF.Functions.Like(sibling.Name, pattern))),
            Name = document.Name,
            Extension = document.Extension,
            MimeType = document.MimeType,
            Size = document.Size,
            Type = document.Type,
            DateCreated = document.DateCreated
        };
    }
}
