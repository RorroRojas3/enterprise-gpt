using Enterprise.Gpt.Common.Constants.Projects;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Projects;

/// <summary>
/// Request to create a project owned by the caller.
/// </summary>
public record CreateProjectActionDto
{
    /// <summary>Gets the project name. Must be unique among the caller's active projects.</summary>
    public string Name { get; init; } = null!;

    /// <summary>Gets the optional free-text description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the optional standing instructions applied to every conversation in the project.
    /// </summary>
    /// <remarks>
    /// This text reaches the model as a system message on every turn, which is why it is length-capped
    /// — see <see cref="ProjectLimits.InstructionsMaxLength"/>.
    /// </remarks>
    public string? Instructions { get; init; }
}

/// <summary>
/// Validates <see cref="CreateProjectActionDto"/>.
/// </summary>
public class CreateProjectActionDtoValidator : AbstractValidator<CreateProjectActionDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateProjectActionDtoValidator"/> class.
    /// </summary>
    public CreateProjectActionDtoValidator()
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
