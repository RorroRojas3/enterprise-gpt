namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// The names the File Agent is registered under.
/// </summary>
/// <remarks>
/// Here rather than beside the composition in <c>Enterprise.Gpt.Api</c>, because the tool funnel has
/// to check for a name collision before it asks for the tool, and the usage translator attributes a
/// call by the same string.
/// </remarks>
public static class FileAgentToolNames
{
    /// <summary>
    /// The outward tool name the model sees.
    /// </summary>
    /// <remarks>
    /// Its leading segment must not read as an MCP server's prefix: a tool call is attributed back to
    /// a server by the longest matching <c>{server}_</c> prefix, so a catalog server named
    /// <c>file</c> would claim this one's usage. The same acknowledged, unfixable-in-general risk
    /// <c>document_search</c> carries.
    /// </remarks>
    public const string Agent = "file_agent";
}
