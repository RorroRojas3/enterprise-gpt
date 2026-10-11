namespace Enterprise.Gpt.Dto.Actions.Projects;

/// <summary>
/// Request to mark one of the caller's projects as a favourite, or to clear the mark.
/// </summary>
/// <remarks>
/// Not a field on <see cref="UpdateProjectActionDto"/>: that PUT is a full representation, so a rename
/// that omitted the flag would silently un-favourite. No validator: a bool can only fail binding.
/// </remarks>
public record SetProjectFavoriteActionDto
{
    /// <summary>A set, not a toggle, so the request is idempotent.</summary>
    public bool IsFavorite { get; init; }
}
