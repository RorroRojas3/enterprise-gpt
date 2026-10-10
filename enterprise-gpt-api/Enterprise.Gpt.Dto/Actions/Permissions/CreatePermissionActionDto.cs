using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Permissions;

public record CreatePermissionActionDto
{
    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    /// <summary>
    /// Whether the permission should be granted automatically to every self-provisioned user.
    /// </summary>
    public bool IsDefault { get; init; }
}

public class CreatePermissionActionDtoValidator : AbstractValidator<CreatePermissionActionDto>
{
    public CreatePermissionActionDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(256);
        RuleFor(x => x.Description)
            .MaximumLength(1024);
    }
}
