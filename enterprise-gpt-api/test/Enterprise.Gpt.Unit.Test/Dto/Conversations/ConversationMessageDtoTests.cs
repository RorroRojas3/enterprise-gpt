using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Conversations;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Dto.Conversations;

/// <summary>
/// The API has no global string-enum converter (it would turn <c>role</c> into a string), so each string
/// enum on the wire carries its own, and dropping one is a silent breaking change.
/// </summary>
public sealed class ConversationMessageDtoTests
{
    // The API's own defaults: camelCase names, no enum converter.
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(ConversationMessageDto message)
    {
        return JsonSerializer.SerializeToElement(message, Options);
    }

    [Fact]
    public void Role_Serializes_AsItsIntegerValue()
    {
        var element = Serialize(new ConversationMessageDto { Text = "Hi", Role = ChatRoles.Assistant });

        Assert.Equal(JsonValueKind.Number, element.GetProperty("role").ValueKind);
        Assert.Equal((int)ChatRoles.Assistant, element.GetProperty("role").GetInt32());
    }

    /// <summary>
    /// PascalCase from the parameterless converter. The transcript document stores the verdict
    /// camel-cased; the two are separate contracts.
    /// </summary>
    [Fact]
    public void Feedback_Serializes_WithAPascalCasedRatingString()
    {
        var ratedAt = new DateTimeOffset(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);
        var element = Serialize(new ConversationMessageDto
        {
            Text = "Hi",
            Role = ChatRoles.Assistant,
            Feedback = new ConversationMessageFeedbackDto
            {
                Rating = MessageFeedbackRatings.Down,
                DateModified = ratedAt
            }
        });

        var feedback = element.GetProperty("feedback");
        Assert.Equal("Down", feedback.GetProperty("rating").GetString());
        Assert.Equal(ratedAt, feedback.GetProperty("dateModified").GetDateTimeOffset());
    }

    /// <summary>
    /// Absent-as-null rather than an object holding a default verdict: "not rated" and "rated up"
    /// are different facts, and the client draws neither thumb for the first.
    /// </summary>
    [Fact]
    public void Feedback_OnAnUnratedMessage_SerializesAsNull()
    {
        var element = Serialize(new ConversationMessageDto { Text = "Hi", Role = ChatRoles.User });

        Assert.Equal(JsonValueKind.Null, element.GetProperty("feedback").ValueKind);
    }

    [Fact]
    public void TokenAccuracy_Serializes_AsItsDeclaredName()
    {
        var element = Serialize(new ConversationMessageDto
        {
            Text = "Hi",
            Role = ChatRoles.Assistant,
            TokenAccuracy = TokenAccuracies.Estimated
        });

        Assert.Equal("Estimated", element.GetProperty("tokenAccuracy").GetString());
    }
}
