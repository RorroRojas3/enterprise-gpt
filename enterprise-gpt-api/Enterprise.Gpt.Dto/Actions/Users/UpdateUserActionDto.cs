using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Constants.Users;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Users;

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
            .MaximumLength(UserLimits.FirstNameMaxLength)
            .WithMessage($"First name cannot exceed {UserLimits.FirstNameMaxLength} characters.");
        RuleFor(x => x.LastName)
            .NotEmpty()
            .WithMessage("Last name is required.")
            .MaximumLength(UserLimits.LastNameMaxLength)
            .WithMessage($"Last name cannot exceed {UserLimits.LastNameMaxLength} characters.");
        RuleFor(x => x.Email)
            .NotEmpty()
            .WithMessage("Email is required.")
            .EmailAddress()
            .WithMessage("Invalid email format.")
            .MaximumLength(UserLimits.EmailMaxLength)
            .WithMessage($"Email cannot exceed {UserLimits.EmailMaxLength} characters.");
        RuleFor(x => x.PermissionIds)
            .NotNull()
            .WithMessage("Permission ids are required.");
        RuleForEach(x => x.PermissionIds)
            .NotEmpty()
            .WithMessage("Permission ids cannot be empty.");
    }
}
