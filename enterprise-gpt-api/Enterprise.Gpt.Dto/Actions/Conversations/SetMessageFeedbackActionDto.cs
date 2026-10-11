using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

/// <summary>
/// Records, replaces or withdraws the caller's rating of an assistant message.
/// </summary>
/// <remarks>
/// A set, not a toggle, so a retried or duplicated request cannot land on the opposite value.
/// </remarks>
public class SetMessageFeedbackActionDto
{
    /// <summary>
    /// The verdict, or <see langword="null"/> to withdraw a rating already recorded.
    /// </summary>
    /// <remarks>Withdrawing an absent rating succeeds.</remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<MessageFeedbackRatings>))]
    public MessageFeedbackRatings? Rating { get; set; }
}

/// <remarks>
/// Load-bearing: <c>JsonStringEnumConverter</c> accepts undefined integers, so <c>{"rating": 7}</c> would
/// be stored and later written back as a JSON number, breaking the wire format.
/// </remarks>
public class SetMessageFeedbackActionDtoValidator : AbstractValidator<SetMessageFeedbackActionDto>
{
    public SetMessageFeedbackActionDtoValidator()
    {
        RuleFor(x => x.Rating)
            .IsInEnum()
            .WithMessage("Rating must be one of: Up, Down.")
            .When(x => x.Rating.HasValue);
    }
}
