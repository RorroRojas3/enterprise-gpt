using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Conversations;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Dto.Actions.Conversations;

public class SetMessageFeedbackActionValidatorTests
{
    [Theory]
    [InlineData(MessageFeedbackRatings.Up)]
    [InlineData(MessageFeedbackRatings.Down)]
    public void Rating_ADefinedValue_IsAccepted(MessageFeedbackRatings rating)
    {
        var result = new SetMessageFeedbackActionDtoValidator().Validate(new SetMessageFeedbackActionDto { Rating = rating });

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// The string-enum converter accepts integers, undefined ones included, so an unvalidated request
    /// would store a rating the enum has no name for.
    /// </summary>
    [Fact]
    public void Rating_OutsideTheEnum_IsRejected()
    {
        var result = new SetMessageFeedbackActionDtoValidator().Validate(
            new SetMessageFeedbackActionDto { Rating = (MessageFeedbackRatings)7 });

        Assert.Equal(nameof(SetMessageFeedbackActionDto.Rating), Assert.Single(result.Errors).PropertyName);
    }
}
