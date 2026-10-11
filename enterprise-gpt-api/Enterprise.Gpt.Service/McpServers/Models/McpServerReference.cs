namespace Enterprise.Gpt.Service.McpServers.Models;

/// <summary>An MCP server whose tools were leased for a request, with its name at lease time.</summary>
public readonly record struct McpServerReference(Guid Id, string Name);
