using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Permissions;

namespace Enterprise.Gpt.Entity.McpServers;

/// <summary>
/// An MCP (Model Context Protocol) server administrators make available to users.
/// Access is gated by the linked <see cref="Permission"/> row auto-created with the server.
/// </summary>
public class McpServer : BaseModifiedByEntity<Guid>
{
    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string Url { get; set; } = null!;

    public McpAuthTypes AuthType { get; set; }

    /// <summary>
    /// The scope requested during on-behalf-of token acquisition
    /// (e.g. <c>api://{clientId}/access_as_user</c>); <see langword="null"/>
    /// for every other <see cref="AuthType"/>.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Client-side brand mark slug, such as <c>microsoft</c>; <see langword="null"/> for the generic glyph.
    /// An open slug, not an enum, so new icons need no server deploy and unknown keys fall back.
    /// </summary>
    public string? IconKey { get; set; }

    /// <summary>
    /// Extra request headers sent on every call to this server, such as the remote Azure
    /// DevOps server's <c>X-MCP-Toolsets</c> and <c>X-MCP-Readonly</c>; <see langword="null"/>
    /// when none are configured.
    /// </summary>
    /// <remarks>
    /// Configuration, not credentials: validators refuse <c>Authorization</c> and transport-owned headers,
    /// <c>McpToolProvider</c> writes the bearer last, and values are plain text returned only to admins.
    /// Always keyed <see cref="StringComparer.OrdinalIgnoreCase"/>, as header names are case-insensitive.
    /// </remarks>
    public Dictionary<string, string>? Headers { get; set; }

    public ICollection<Permission> Permissions { get; set; } = [];

    /// <summary>
    /// The per-user credentials held for this server; empty unless <see cref="AuthType"/> is
    /// <see cref="McpAuthTypes.UserApiKey"/>.
    /// </summary>
    public ICollection<UserMcpCredential> UserCredentials { get; set; } = [];
}
