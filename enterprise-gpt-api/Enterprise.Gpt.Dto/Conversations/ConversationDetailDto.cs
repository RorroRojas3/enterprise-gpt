namespace Enterprise.Gpt.Dto.Conversations;

/// <summary>
/// A single conversation read on its own, carrying the selections needed to resume it.
/// </summary>
/// <remarks>
/// Separate from <see cref="ConversationDto"/> because <see cref="McpServerIds"/> costs a subquery per
/// conversation, and an unfilled list on the listing shape would read as "none selected".
/// </remarks>
public class ConversationDetailDto : ConversationDto
{
    /// <summary>
    /// The MCP servers attached to the conversation's most recent turn. Empty when no turn has
    /// run yet or none were selected.
    /// </summary>
    public List<Guid> McpServerIds { get; set; } = [];
}
