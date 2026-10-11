using System.Linq.Expressions;
using Enterprise.Gpt.Dto.Conversations;
using Enterprise.Gpt.Entity.Conversations;

namespace Enterprise.Gpt.Service.Conversations;

public static class ConversationMapper
{
    /// <summary>
    /// Projection from <see cref="Conversation"/> to <see cref="ConversationDto"/> for
    /// <c>IQueryable.Select</c>.
    /// </summary>
    /// <remarks>
    /// Calling <see cref="MapToChatDto"/> in a projection is client-evaluated and materializes whole entities.
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

    #region Public static methods

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

    #endregion
}
