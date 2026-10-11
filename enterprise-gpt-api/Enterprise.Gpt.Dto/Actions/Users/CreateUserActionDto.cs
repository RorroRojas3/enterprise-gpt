using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Constants.Users;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Users;

/// <summary>
/// Administrator request to provision a user before first sign-in. The object id and display name are
/// resolved from Microsoft Graph so the row keys on the object id the user will present later.
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
