using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.Conversations;

/// <summary>
/// A reader's rating of one assistant message.
/// </summary>
/// <remarks>
/// A nested block rather than a bare <c>rating</c> field, so fields can join the verdict later without a
/// breaking change to the message shape.
/// </remarks>
public class ConversationMessageFeedbackDto
{
    /// <remarks>
    /// A property-level string converter, for the same reason as
    /// <see cref="ConversationMessageDto.TokenAccuracy"/>.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<MessageFeedbackRatings>))]
    public MessageFeedbackRatings Rating { get; set; }

    public DateTimeOffset DateModified { get; set; }
}
