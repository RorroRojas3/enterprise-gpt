using Enterprise.Gpt.Common.Constants.Projects;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Projects;

/// <summary>
/// Request to create a project owned by the caller.
/// </summary>
public record CreateProjectActionDto
{
    /// <summary>Must be unique among the caller's active projects.</summary>
    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    /// <summary>
    /// Standing instructions sent as a system message on every turn of the project's conversations.
    /// </summary>
    public string? Instructions { get; init; }
}

public class CreateProjectActionDtoValidator : AbstractValidator<CreateProjectActionDto>
{
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
