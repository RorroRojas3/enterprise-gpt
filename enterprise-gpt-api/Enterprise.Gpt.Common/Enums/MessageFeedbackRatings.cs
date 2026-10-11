namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// A reader's verdict on an assistant message.
/// </summary>
/// <remarks>
/// Binary with no comment, which would bring retention and moderation questions. No <c>Description</c>
/// attributes: the stored and wire casings come from JSON enum converters, which read none.
/// </remarks>
public enum MessageFeedbackRatings
{
    Up = 1,

    Down = 2
}
