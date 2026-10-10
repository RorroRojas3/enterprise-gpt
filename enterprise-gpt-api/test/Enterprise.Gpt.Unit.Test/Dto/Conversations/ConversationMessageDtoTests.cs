using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Serialization;

/// <summary>
/// The HTTP shape of a transcript message, which is a contract clients match on rather than a
/// formatting preference. The API registers no global string-enum converter — one would turn
/// <c>role</c> from a number into a string and break every client reading it — so each enum that
/// belongs on the wire as a string has to say so on its own property, and a converter dropped from
/// one of them is a silent breaking change.
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
    /// PascalCase, from the converter's parameterless form, which writes the member names as
    /// declared. The transcript document underneath stores the same verdict camel-cased; the two
    /// are separate contracts and neither may be inferred from the other.
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
