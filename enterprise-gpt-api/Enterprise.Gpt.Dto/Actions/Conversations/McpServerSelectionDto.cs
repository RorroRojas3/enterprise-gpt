using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Actions.Conversations;

/// <summary>
/// Identifies an MCP server selected for a chat request. Deliberately separate from the
/// response-shaped <see cref="McpDto"/>: a request only needs the id, and reusing the
/// response DTO would advertise <c>Name</c> as required in the OpenAPI document while the
/// validator does not enforce it.
/// </summary>
public class McpServerSelectionDto
{
    public Guid Id { get; set; }
}
