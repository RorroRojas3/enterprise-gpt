using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

/// <summary>
/// Records, replaces or withdraws the caller's rating of an assistant message (US-1102).
/// </summary>
/// <remarks>
/// A set, not a toggle: the body carries the state being asked for, so a retried or duplicated
/// request cannot land on the opposite value. Re-submitting replaces the stored rating rather
/// than adding to it, which is what a unique index on the projection row guarantees.
/// </remarks>
public class SetMessageFeedbackActionDto
{
    /// <summary>
    /// The verdict, or <see langword="null"/> to withdraw a rating already recorded.
    /// </summary>
    /// <remarks>
    /// Withdrawing an absent rating is not an error: the route is a set, and asking for the
    /// state it is already in succeeds.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<MessageFeedbackRatings>))]
    public MessageFeedbackRatings? Rating { get; set; }
}

/// <summary>
/// Validates <see cref="SetMessageFeedbackActionDto"/>.
/// </summary>
/// <remarks>
/// One rule, and it is load-bearing rather than defensive. <c>JsonStringEnumConverter</c>
/// allows integer values by default, and Microsoft documents that as allowing <em>undefined</em>
/// ones — so without this, <c>{"rating": 7}</c> binds to a value the enum does not define,
/// passes every other check, and is written to both stores. It then comes back out of the
/// transcript as a JSON number, because a string converter has no name to write for it, which
/// breaks the wire format for every client reading that field.
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
