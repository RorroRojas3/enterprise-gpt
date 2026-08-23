namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// User-facing representation of an MCP server, used for the tools listing and for
    /// selecting servers in chat requests. Connection details are deliberately not exposed;
    /// <see cref="IconKey"/> is presentation rather than a connection detail, which is why it
    /// crosses to this shape while <c>Url</c>, <c>AuthType</c> and <c>Scope</c> do not.
    /// </summary>
    public record McpDto
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = null!;

        public string? Description { get; init; }

        public string? IconKey { get; init; }
    }
}
