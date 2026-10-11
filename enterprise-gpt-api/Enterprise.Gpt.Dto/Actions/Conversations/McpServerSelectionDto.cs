using Enterprise.Gpt.Dto.McpServers;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

/// <summary>
/// Identifies an MCP server selected for a chat request. Separate from <see cref="McpDto"/> so the
/// OpenAPI document does not advertise a required <c>Name</c> the validator never checks.
/// </summary>
public class McpServerSelectionDto
{
    public Guid Id { get; set; }
}
