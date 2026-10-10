namespace Enterprise.Gpt.Service.McpServers.Models;

/// <summary>
/// Identifies an MCP server whose tools were leased for a request, captured as the server
/// stood at that moment.
/// </summary>
/// <param name="Id">The server's unique identifier.</param>
/// <param name="Name">The server's name at lease time.</param>
public readonly record struct McpServerReference(Guid Id, string Name);
