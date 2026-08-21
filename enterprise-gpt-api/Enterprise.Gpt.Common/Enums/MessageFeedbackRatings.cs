namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// A reader's verdict on an assistant message.
/// </summary>
/// <remarks>
/// Deliberately binary, with no free-text comment: a comment would carry its own retention and
/// moderation questions, and adding one later is a new field beside the verdict rather than a
/// change to this enum.
/// <para>
/// Carries no <c>Description</c> attributes. The lowercase form a transcript document stores comes
/// from the Cosmos serializer's camel-casing enum converter, and the PascalCase form on the HTTP
/// wire from the converter on the DTO property — neither reads an attribute, so one here would be
/// metadata a reader could reasonably mistake for the source of the wire format.
/// </para>
/// </remarks>
public enum MessageFeedbackRatings
{
    /// <summary>The answer helped.</summary>
    Up = 1,

    /// <summary>The answer did not help.</summary>
    Down = 2
}
