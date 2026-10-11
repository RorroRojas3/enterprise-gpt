namespace Enterprise.Gpt.Common.Constants.Projects;

/// <summary>
/// Column widths of a project, shared by its configuration and the project validators.
/// </summary>
public static class ProjectLimits
{
    public const int NameMaxLength = 256;

    public const int DescriptionMaxLength = 1024;

    /// <summary>Maximum instructions length. Not a storage limit (the column is <c>nvarchar(max)</c>): the instructions are sent on every turn, so an uncapped value could crowd the conversation out of the context window.</summary>
    public const int InstructionsMaxLength = 8000;
}
