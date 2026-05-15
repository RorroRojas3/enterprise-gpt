using FluentValidation;
using System.Text.Json.Serialization;

namespace RR.AI_Chat.Dto.Actions.User
{
    public class UpdateUserActionDto
    {
        [JsonPropertyName("firstName")]
        public string FirstName { get; set; } = null!;
        
        [JsonPropertyName("lastName")]
        public string LastName { get; set; } = null!;

        [JsonPropertyName("email")]
        public string Email { get; set; } = null!;
    }

    public class UpdateUserActionDtoValidator : AbstractValidator<UpdateUserActionDto>
    {
        public UpdateUserActionDtoValidator()
        {
            RuleFor(x => x.FirstName)
                .NotEmpty()
                .WithMessage("First name is required.")
                .MaximumLength(256)
                .WithMessage("First name cannot exceed 256 characters.");
            RuleFor(x => x.LastName)
                .NotEmpty()
                .WithMessage("Last name is required.")
                .MaximumLength(256)
                .WithMessage("Last name cannot exceed 256 characters.");
            RuleFor(x => x.Email)
                .NotEmpty()
                .WithMessage("Email is required.")
                .EmailAddress()
                .WithMessage("Invalid email format.")
                .MaximumLength(512)
                .WithMessage("Email cannot exceed 512 characters.");
        }
    }
}
