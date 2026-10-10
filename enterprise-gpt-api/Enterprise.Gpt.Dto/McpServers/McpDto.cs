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

        /// <summary>
        /// Whether using this server requires a credential the caller supplies themselves.
        /// </summary>
        /// <remarks>
        /// The auth type itself stays off this shape — which mechanism authenticates a server is
        /// a connection detail. What the caller has to know is only whether they must act.
        /// </remarks>
        public bool RequiresUserApiKey { get; init; }

        /// <summary>
        /// Whether the caller has a usable credential stored for this server. Always
        /// <see langword="false"/> when <see cref="RequiresUserApiKey"/> is
        /// <see langword="false"/>, and false again once the server has rejected the stored one.
        /// </summary>
        public bool HasUserApiKey { get; init; }

        /// <summary>
        /// Last four characters of the caller's stored credential, so they can tell which key is
        /// held; <see langword="null"/> when none is.
        /// </summary>
        public string? ApiKeyHint { get; init; }
    }

    /// <summary>
    /// What the caller may know about their own credential for one MCP server. Carries neither
    /// the credential nor its ciphertext.
    /// </summary>
    public record McpCredentialStatusDto
    {
        public Guid McpServerId { get; init; }

        public bool HasApiKey { get; init; }

        public string? ApiKeyHint { get; init; }

        /// <summary>When the credential was last saved; <see langword="null"/> when none is held.</summary>
        public DateTimeOffset? DateModified { get; init; }
    }
}
