using FluentValidation;

namespace RR.AI_Chat.Dto.Actions.Model
{
    public class CreateModelActionDto
    {
        public string Name { get; set; } = null!;

        public string Description { get; set; } = null!;

        public bool IsToolEnabled { get; set; } = false;
    }

    public class CreateModelActionDtoValidator : AbstractValidator<CreateModelActionDto>
    {
        public CreateModelActionDtoValidator()
        {
            RuleFor(x => x.Name)
                .NotEmpty()
                .MaximumLength(100);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(500);
        }
    }

    public class UpdateModelActionDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public string Description { get; set; } = null!;

        public bool IsToolEnabled { get; set;} = false;
    }

    public class UpdateModelActionDtoValidator : AbstractValidator<UpdateModelActionDto>
    {
        public UpdateModelActionDtoValidator()
        {
            RuleFor(x => x.Id)
                .NotEmpty();
            RuleFor(x => x.Name)
                .NotEmpty()
                .MaximumLength(100);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(500);
        }
    }
}