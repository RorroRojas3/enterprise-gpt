using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Model
{
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

        public bool IsDefault { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 input tokens in USD. Omit it to leave the
        /// model unpriced, which is not the same as free.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 output tokens in USD, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
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
                .MaximumLength(256);
            RuleFor(x => x.DeploymentName)
                .NotEmpty()
                .MaximumLength(512);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.ContextWindowSize)
                .GreaterThan(0);
            RuleFor(x => x.MaxOutputTokens)
                .GreaterThan(0);
            // Zero is accepted where the sibling rules demand a positive value: a genuinely free
            // deployment exists, and omitting the price is how "unpriced" is expressed instead.
            RuleFor(x => x.InputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.InputPricePerMillionTokens.HasValue);
            RuleFor(x => x.OutputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.OutputPricePerMillionTokens.HasValue);
        }
    }

    public record UpdateModelActionDto
    {
        public Guid ProviderId { get; init; }

        public string Name { get; init; } = null!;

        public string DeploymentName { get; init; } = null!;

        public string Description { get; init; } = null!;

        public decimal ContextWindowSize { get; init; }

        public decimal MaxOutputTokens { get; init; }

        public bool IsToolEnabled { get; init; }

        public bool IsReasoningEnabled { get; init; }

        public bool IsDefault { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 input tokens in USD. Omitting it clears the
        /// stored price, matching the full-representation semantics of the rest of this request.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 output tokens in USD, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        public decimal? OutputPricePerMillionTokens { get; init; }
    }

    public class UpdateModelActionDtoValidator : AbstractValidator<UpdateModelActionDto>
    {
        public UpdateModelActionDtoValidator()
        {
            RuleFor(x => x.ProviderId)
                .NotEmpty();
            RuleFor(x => x.Name)
                .NotEmpty()
                .MaximumLength(256);
            RuleFor(x => x.DeploymentName)
                .NotEmpty()
                .MaximumLength(512);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.ContextWindowSize)
                .GreaterThan(0);
            RuleFor(x => x.MaxOutputTokens)
                .GreaterThan(0);
            // Zero is accepted where the sibling rules demand a positive value: a genuinely free
            // deployment exists, and omitting the price is how "unpriced" is expressed instead.
            RuleFor(x => x.InputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.InputPricePerMillionTokens.HasValue);
            RuleFor(x => x.OutputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.OutputPricePerMillionTokens.HasValue);
        }
    }
}
