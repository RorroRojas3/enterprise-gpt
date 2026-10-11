using Enterprise.Gpt.Common.Constants.Projects;
using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Projects;

/// <summary>
/// Request to update one of the caller's projects. Every field is replaced, so an omitted optional
/// field clears the stored value.
/// </summary>
public record UpdateProjectActionDto
{
    /// <summary>Must be unique among the caller's active projects.</summary>
    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    /// <summary>
    /// Standing instructions for the project's conversations, read afresh each turn, so a change reaches
    /// existing conversations.
    /// </summary>
    public string? Instructions { get; init; }
}

public class UpdateProjectActionDtoValidator : AbstractValidator<UpdateProjectActionDto>
{
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
