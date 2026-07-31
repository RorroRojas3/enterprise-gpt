namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// User-facing representation of an MCP server, used for the tools listing and for
    /// selecting servers in chat requests. Connection details are deliberately not exposed.
    /// </summary>
    public record McpDto
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = null!;

        public string? Description { get; init; }
    }
}
