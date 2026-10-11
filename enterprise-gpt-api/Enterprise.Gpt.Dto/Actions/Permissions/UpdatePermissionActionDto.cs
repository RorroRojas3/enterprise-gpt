using Enterprise.Gpt.Common.Constants.Permissions;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Permissions;

public record UpdatePermissionActionDto
{
    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    /// <summary>
    /// Whether the permission should be granted automatically to every self-provisioned user.
    /// </summary>
    public bool IsDefault { get; init; }
}

public class UpdatePermissionActionDtoValidator : AbstractValidator<UpdatePermissionActionDto>
{
    public UpdatePermissionActionDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(PermissionLimits.NameMaxLength);
        RuleFor(x => x.Description)
            .MaximumLength(PermissionLimits.DescriptionMaxLength);
    }
}
