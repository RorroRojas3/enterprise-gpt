using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Project;

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
    /// — see <see cref="ProjectFieldLengths.Instructions"/>.
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
            .MaximumLength(ProjectFieldLengths.Name);
        RuleFor(x => x.Description)
            .MaximumLength(ProjectFieldLengths.Description);
        RuleFor(x => x.Instructions)
            .MaximumLength(ProjectFieldLengths.Instructions);
    }
}

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
            .MaximumLength(ProjectFieldLengths.Name);
        RuleFor(x => x.Description)
            .MaximumLength(ProjectFieldLengths.Description);
        RuleFor(x => x.Instructions)
            .MaximumLength(ProjectFieldLengths.Instructions);
    }
}

/// <summary>
/// Field limits shared by the create and update validators.
/// </summary>
/// <remarks>
/// <see cref="Name"/> and <see cref="Description"/> must match the column lengths on
/// <c>Enterprise.Gpt.Entity.Project</c>, or an over-long value fails as a truncation error at save
/// time instead of as a validation problem.
/// </remarks>
public static class ProjectFieldLengths
{
    /// <summary>Maximum project name length, matching <c>Core.Project.Name</c>.</summary>
    public const int Name = 256;

    /// <summary>Maximum description length, matching <c>Core.Project.Description</c>.</summary>
    public const int Description = 1024;

    /// <summary>
    /// Maximum instructions length.
    /// </summary>
    /// <remarks>
    /// The column is <c>nvarchar(max)</c>, so this bound is not a storage limit. It exists because the
    /// instructions are sent on every turn: without a cap a single project could consume the model's
    /// context window on every message and displace the conversation itself.
    /// </remarks>
    public const int Instructions = 8000;
}
