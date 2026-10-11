using System.Linq.Expressions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Entity.Documents;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Service.Documents;

/// <summary>
/// Maps <see cref="ConversationDocument"/> to its DTOs, in both the materialized and the
/// server-side-projection forms.
/// </summary>
public static class ConversationDocumentMapper
{
    /// <summary>Server-side projection; omits chunk data so listings never load embeddings.</summary>
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

    #region Public static methods

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
    /// Server-side projection with the conversation's name and its document count under the same filters
    /// (hence a method: the count closes over them). Omits chunk data.
    /// </summary>
    public static Expression<Func<ConversationDocument, UserDocumentDto>> MapToUserDocumentDtoExpression(
        ConversationDocumentTypes? origin, string? name)
    {
        // Hoisted so EF parameterizes plain values instead of a per-row null check on a nullable enum.
        var hasOrigin = origin.HasValue;
        var originType = origin.GetValueOrDefault();
        var pattern = string.IsNullOrWhiteSpace(name) ? null : $"%{name}%";

        return document => new UserDocumentDto
        {
            Id = document.Id,
            ConversationId = document.ConversationId,
            ConversationName = document.Conversation.Name,
            // No owner predicate: siblings share the conversation's owner. Uses the
            // (ConversationId, DateDeactivated) index.
            ConversationDocumentCount = document.Conversation.Documents.Count(sibling =>
                (!hasOrigin || sibling.Type == originType)
                && (pattern == null || EF.Functions.Like(sibling.Name, pattern))),
            Name = document.Name,
            Extension = document.Extension,
            MimeType = document.MimeType,
            Size = document.Size,
            Type = document.Type,
            DateCreated = document.DateCreated
        };
    }

    #endregion
}
