namespace Enterprise.Gpt.Service.Agents.Constants;

/// <summary>The names the File Agent is registered under.</summary>
/// <remarks>
/// In the service layer because the tool funnel checks for name collisions and the usage translator
/// attributes calls by the same string.
/// </remarks>
public static class FileAgentToolNames
{
    /// <summary>The outward tool name the model sees.</summary>
    /// <remarks>
    /// Usage is attributed by the longest <c>{server}_</c> prefix, so a catalog server named <c>file</c>
    /// would claim this tool's usage (the same known risk as <c>document_search</c>).
    /// </remarks>
    public const string Agent = "file_agent";
}
