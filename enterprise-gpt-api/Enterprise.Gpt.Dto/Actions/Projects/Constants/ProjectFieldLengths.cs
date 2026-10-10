using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Projects.Constants;

/// <summary>
/// Field limits shared by the create and update validators.
/// </summary>
/// <remarks>
/// <see cref="Name"/> and <see cref="Description"/> must match the column lengths on
/// <c>Enterprise.Gpt.Entity.Projects.Project</c>, or an over-long value fails as a truncation error at save
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
