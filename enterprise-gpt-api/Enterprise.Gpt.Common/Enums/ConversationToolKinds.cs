namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// Specifies which category of tool a <c>ConversationUsageToolCall</c> row accounts for.
/// </summary>
/// <remarks>
/// Mirrors <c>Andes.Extensions.AI.ToolKind</c> value for value, deliberately rather than storing
/// the library's enum directly: the persisted numbers are an on-disk contract that must not move
/// when the package is upgraded, and the entity layer does not otherwise depend on the tracking
/// middleware.
/// </remarks>
public enum ConversationToolKinds
{
    /// <summary>
    /// The category could not be determined — a tool declaration the pipeline could not invoke,
    /// recognized only by observing the model's function call.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A plain in-process function tool.
    /// </summary>
    Function = 1,

    /// <summary>
    /// A tool served by a Model Context Protocol server. What the server spends internally is
    /// opaque, so such a call reports usage only when the server itself surfaces it.
    /// </summary>
    McpTool = 2,

    /// <summary>
    /// An agent exposed to the model as a function. Its run may use a different model than the
    /// calling assistant and may invoke tools of its own, which appear as child rows.
    /// </summary>
    Agent = 3
}
