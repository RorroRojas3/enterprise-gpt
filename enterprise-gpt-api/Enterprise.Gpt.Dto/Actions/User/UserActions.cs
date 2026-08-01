using FluentValidation;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Actions.User
{
    /// <summary>
    /// Administrator request to provision a user ahead of their first sign-in. Only the email and
    /// the permissions to assign are supplied; the object id and display name are resolved from
    /// Microsoft Graph so the row keys on the same object id the user will present later.
    /// </summary>
    public record CreateUserActionDto
    {
        [JsonPropertyName("email")]
        public string Email { get; init; } = null!;

        [JsonPropertyName("permissionIds")]
        public IReadOnlyList<Guid> PermissionIds { get; init; } = [];
    }

    public class CreateUserActionDtoValidator : AbstractValidator<CreateUserActionDto>
    {
        public CreateUserActionDtoValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty()
                .WithMessage("Email is required.")
                .EmailAddress()
                .WithMessage("Invalid email format.")
                .MaximumLength(512)
                .WithMessage("Email cannot exceed 512 characters.");
            RuleFor(x => x.PermissionIds)
                .NotNull()
                .WithMessage("Permission ids are required.");
            RuleForEach(x => x.PermissionIds)
                .NotEmpty()
                .WithMessage("Permission ids cannot be empty.");
        }
    }

    /// <summary>
    /// Administrator request to update a user's profile and replace their permission set.
    /// <see cref="PermissionIds"/> is the complete desired set: grants missing from it are
    /// revoked, so an empty list strips every permission the user holds.
    /// </summary>
    public record UpdateUserActionDto
    {
        [JsonPropertyName("firstName")]
        public string FirstName { get; init; } = null!;

        [JsonPropertyName("lastName")]
        public string LastName { get; init; } = null!;

        [JsonPropertyName("email")]
        public string Email { get; init; } = null!;

        [JsonPropertyName("permissionIds")]
        public IReadOnlyList<Guid> PermissionIds { get; init; } = [];
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
            RuleFor(x => x.PermissionIds)
                .NotNull()
                .WithMessage("Permission ids are required.");
            RuleForEach(x => x.PermissionIds)
                .NotEmpty()
                .WithMessage("Permission ids cannot be empty.");
        }
    }
}
