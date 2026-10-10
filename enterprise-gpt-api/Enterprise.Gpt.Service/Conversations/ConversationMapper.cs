using Enterprise.Gpt.Dto;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Enterprise.Gpt.Service.Conversations;

public static class ConversationMapper
{
    public static ConversationDto MapToChatDto(this Conversation source)
    {
        return new ConversationDto
        {
            Id = source.Id,
            ProjectId = source.ProjectId,
            ModelId = source.ModelId,
            Name = source.Name,
            IsFavorite = source.IsFavorite,
            DateCreated = source.DateCreated,
            DateModified = source.DateModified
        };
    }

    /// <summary>
    /// LINQ projection from <see cref="Conversation"/> to <see cref="ConversationDto"/> for use
    /// inside <c>IQueryable.Select(...)</c>.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="MapToChatDto"/> rather than replacing it: a query that calls the
    /// method in a top-level projection is client-evaluated, so it materializes whole entities —
    /// including the rowversion and the token counters — to build a five-field DTO.
    /// </remarks>
    public static Expression<Func<Conversation, ConversationDto>> MapToChatDtoExpression { get; } =
        source => new ConversationDto
        {
            Id = source.Id,
            ProjectId = source.ProjectId,
            ModelId = source.ModelId,
            Name = source.Name,
            IsFavorite = source.IsFavorite,
            DateCreated = source.DateCreated,
            DateModified = source.DateModified
        };
}
