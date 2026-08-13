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

        /// <summary>
        /// Gets the price in USD per one million input tokens. Omit it to leave the model
        /// unpriced, which is not the same as pricing it at zero.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// Gets the price in USD per one million output tokens, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        public decimal? OutputPricePerMillionTokens { get; init; }

        public bool IsToolEnabled { get; init; }

        public bool IsDefault { get; init; }
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

        /// <summary>
        /// Gets the price in USD per one million input tokens. Omitting it clears the stored
        /// price, matching the full-representation semantics the rest of this DTO uses; an
        /// unpriced model is not the same as a free one.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// Gets the price in USD per one million output tokens, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        public decimal? OutputPricePerMillionTokens { get; init; }

        public bool IsToolEnabled { get; init; }

        public bool IsDefault { get; init; }
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
            RuleFor(x => x.InputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.InputPricePerMillionTokens.HasValue);
            RuleFor(x => x.OutputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.OutputPricePerMillionTokens.HasValue);
        }
    }
}
