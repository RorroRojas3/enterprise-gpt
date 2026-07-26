using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Model
{
    public record CreateModelActionDto
    {
        public Guid ProviderId { get; init; }

        public string Name { get; init; } = null!;

        public string DisplayName { get; init; } = null!;

        public string Description { get; init; } = null!;

        public decimal ContextWindowSize { get; init; }

        public decimal MaxOutputTokens { get; init; }

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
            RuleFor(x => x.DisplayName)
                .NotEmpty()
                .MaximumLength(256);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.ContextWindowSize)
                .GreaterThan(0);
            RuleFor(x => x.MaxOutputTokens)
                .GreaterThan(0);
        }
    }

    public record UpdateModelActionDto
    {
        public Guid ProviderId { get; init; }

        public string Name { get; init; } = null!;

        public string DisplayName { get; init; } = null!;

        public string Description { get; init; } = null!;

        public decimal ContextWindowSize { get; init; }

        public decimal MaxOutputTokens { get; init; }

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
            RuleFor(x => x.DisplayName)
                .NotEmpty()
                .MaximumLength(256);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.ContextWindowSize)
                .GreaterThan(0);
            RuleFor(x => x.MaxOutputTokens)
                .GreaterThan(0);
        }
    }
}
