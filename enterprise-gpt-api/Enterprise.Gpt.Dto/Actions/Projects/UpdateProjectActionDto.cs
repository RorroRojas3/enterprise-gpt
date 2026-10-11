using Enterprise.Gpt.Common.Constants.Projects;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Projects;

/// <summary>
/// Request to update one of the caller's projects. Every field is replaced, so an omitted optional
/// field clears the stored value.
/// </summary>
public record UpdateProjectActionDto
{
    /// <summary>Gets the project name. Must be unique among the caller's active projects.</summary>
    public string Name { get; init; } = null!;

    /// <summary>Gets the optional free-text description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the optional standing instructions applied to every conversation in the project. Read
    /// afresh on each turn, so a change here affects conversations that already exist.
    /// </summary>
    public string? Instructions { get; init; }
}

/// <summary>
/// Validates <see cref="UpdateProjectActionDto"/>.
/// </summary>
public class UpdateProjectActionDtoValidator : AbstractValidator<UpdateProjectActionDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateProjectActionDtoValidator"/> class.
    /// </summary>
    public UpdateProjectActionDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(ProjectLimits.NameMaxLength);
        RuleFor(x => x.Description)
            .MaximumLength(ProjectLimits.DescriptionMaxLength);
        RuleFor(x => x.Instructions)
            .MaximumLength(ProjectLimits.InstructionsMaxLength);
    }
}
