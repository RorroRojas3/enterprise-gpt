using Enterprise.Gpt.Common.Constants.ChatModels;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.ChatModels;

public record CreateModelActionDto
{
    public Guid ProviderId { get; init; }

    public string Name { get; init; } = null!;

    public string DeploymentName { get; init; } = null!;

    public string Description { get; init; } = null!;

    public decimal ContextWindowSize { get; init; }

    public decimal MaxOutputTokens { get; init; }

    public bool IsToolEnabled { get; init; }

    public bool IsReasoningEnabled { get; init; }

    /// <summary>Whether the deployment is offered in the chat model picker.</summary>
    /// <remarks>
    /// Defaults to <see langword="true"/> so an omitted field never hides a model. The cost: omitting it
    /// reveals a hidden model, which is why <c>ModelService</c> guards the summarizer row.
    /// </remarks>
    public bool IsUserSelectable { get; init; } = true;

    public bool IsDefault { get; init; }

    /// <summary>USD per 1,000,000 input tokens; omitted means unpriced, which is not free.</summary>
    public decimal? InputPricePerMillionTokens { get; init; }

    /// <summary>
    /// USD per 1,000,000 output tokens, on the same terms as <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    public decimal? OutputPricePerMillionTokens { get; init; }
}

public class CreateModelActionDtoValidator : AbstractValidator<CreateModelActionDto>
{
    public CreateModelActionDtoValidator()
    {
        RuleFor(x => x.ProviderId)
            .NotEmpty();
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(ModelLimits.NameMaxLength);
        RuleFor(x => x.DeploymentName)
            .NotEmpty()
            .MaximumLength(ModelLimits.DeploymentNameMaxLength);
        RuleFor(x => x.Description)
            .NotEmpty()
            .MaximumLength(ModelLimits.DescriptionMaxLength);
        RuleFor(x => x.ContextWindowSize)
            .GreaterThan(0);
        RuleFor(x => x.MaxOutputTokens)
            .GreaterThan(0);
        // A hidden default resolves to nothing in the picker, leaving the composer unable to send. Refused
        // rather than corrected because only the caller knows which of the two intentions to keep.
        RuleFor(x => x.IsDefault)
            .Equal(false)
            .When(x => !x.IsUserSelectable)
            .WithMessage("A model hidden from the model picker cannot be the default model.");
        // Zero is a genuinely free deployment; "unpriced" is expressed by omitting the price.
        RuleFor(x => x.InputPricePerMillionTokens)
            .GreaterThanOrEqualTo(0)
            .When(x => x.InputPricePerMillionTokens.HasValue);
        RuleFor(x => x.OutputPricePerMillionTokens)
            .GreaterThanOrEqualTo(0)
            .When(x => x.OutputPricePerMillionTokens.HasValue);
    }
}
