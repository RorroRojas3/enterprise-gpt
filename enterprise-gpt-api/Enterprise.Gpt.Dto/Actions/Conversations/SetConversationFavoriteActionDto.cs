namespace Enterprise.Gpt.Dto.Actions.Conversations;

/// <summary>
/// Sets whether a conversation is one of the caller's favourites.
/// </summary>
/// <remarks>
/// Not a field on <see cref="UpdateConversationActionDto"/>: that PUT is a full representation, so a
/// rename that omitted the flag would silently un-favourite.
/// </remarks>
public class SetConversationFavoriteActionDto
{
    public bool IsFavorite { get; set; }
}
