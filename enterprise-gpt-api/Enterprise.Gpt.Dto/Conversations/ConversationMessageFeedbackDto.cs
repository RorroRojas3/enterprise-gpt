using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.Conversations;

/// <summary>
/// A reader's rating of one assistant message.
/// </summary>
/// <remarks>
/// A nested block rather than a bare <c>rating</c> field, so a later story can add a comment or
/// a category beside the verdict without a breaking change to the message shape.
/// </remarks>
public class ConversationMessageFeedbackDto
{
    /// <summary>
    /// The verdict.
    /// </summary>
    /// <remarks>
    /// Serialized as a string for the same reason
    /// <see cref="ConversationMessageDto.TokenAccuracy"/> is, and by the same property-level
    /// converter: the API registers no global string-enum converter, because one would turn
    /// <see cref="ConversationMessageDto.Role"/> from a number into a string.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<MessageFeedbackRatings>))]
    public MessageFeedbackRatings Rating { get; set; }

    /// <summary>
    /// When the rating was last set.
    /// </summary>
    public DateTimeOffset DateModified { get; set; }
}
