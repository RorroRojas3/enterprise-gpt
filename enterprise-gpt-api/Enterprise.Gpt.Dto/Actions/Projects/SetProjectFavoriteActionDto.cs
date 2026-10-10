using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Projects;

/// <summary>
/// Request to mark one of the caller's projects as a favourite, or to clear the mark.
/// </summary>
/// <remarks>
/// Deliberately its own action rather than a field on <see cref="UpdateProjectActionDto"/>: that PUT
/// is a full representation, so a client renaming a project without echoing the flag back would
/// silently un-favourite it. There is no validator — <see cref="IsFavorite"/> is a
/// <see cref="bool"/>, so the only 400 it can produce is a binding failure, which carries no
/// <c>errors</c> dictionary.
/// </remarks>
public record SetProjectFavoriteActionDto
{
    /// <summary>Gets the state being asked for. A set, not a toggle, so the request is idempotent.</summary>
    public bool IsFavorite { get; init; }
}
