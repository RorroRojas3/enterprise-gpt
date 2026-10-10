namespace Enterprise.Gpt.Dto.Actions.Conversations;

/// <summary>
/// Sets whether a conversation is one of the caller's favourites.
/// </summary>
/// <remarks>
/// Deliberately its own action rather than a field on <see cref="UpdateConversationActionDto"/>:
/// that PUT is a full representation, so a client renaming a conversation without echoing the
/// flag back would silently un-favourite it.
/// </remarks>
public class SetConversationFavoriteActionDto
{
    public bool IsFavorite { get; set; }
}
