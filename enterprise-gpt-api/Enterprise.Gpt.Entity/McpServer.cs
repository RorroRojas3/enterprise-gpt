using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity
{
    /// <summary>
    /// An MCP (Model Context Protocol) server administrators make available to users.
    /// Access is gated by the linked <see cref="Permission"/> row auto-created with the server.
    /// </summary>
    [Table(nameof(McpServer), Schema = "Core.Ref")]
    public class McpServer : BaseModifiedByEntity
    {
        [StringLength(256)]
        public string Name { get; set; } = null!;

        [StringLength(1024)]
        public string Description { get; set; } = null!;

        [StringLength(2048)]
        public string Url { get; set; } = null!;

        public McpAuthTypes AuthType { get; set; }

        /// <summary>
        /// The scope requested during on-behalf-of token acquisition
        /// (e.g. <c>api://{clientId}/access_as_user</c>); <see langword="null"/>
        /// for every other <see cref="AuthType"/>.
        /// </summary>
        [StringLength(512)]
        public string? Scope { get; set; }

        /// <summary>
        /// Slug naming the brand mark the client draws for this server, such as
        /// <c>microsoft</c>; <see langword="null"/> for the generic glyph. The artwork lives in
        /// the client, so this is deliberately an open slug rather than an enum — shipping a new
        /// icon must not require a server deploy, and a key this client does not recognise
        /// degrades to the generic glyph rather than failing.
        /// </summary>
        [StringLength(64)]
        public string? IconKey { get; set; }

        /// <summary>
        /// Extra request headers sent on every call to this server, such as the remote Azure
        /// DevOps server's <c>X-MCP-Toolsets</c> and <c>X-MCP-Readonly</c>; <see langword="null"/>
        /// when none are configured.
        /// </summary>
        /// <remarks>
        /// Configuration, deliberately not credentials. The validators refuse
        /// <c>Authorization</c> and the other headers the transport owns, and
        /// <c>McpToolProvider</c> writes the on-behalf-of bearer last regardless, so a stored
        /// header can never displace it. Values are persisted in plain text and are only ever
        /// returned on the administrator-gated routes.
        ///
        /// Keyed <see cref="StringComparer.OrdinalIgnoreCase"/> everywhere it is constructed,
        /// because HTTP header names are case-insensitive and two spellings of one header are
        /// the same header.
        /// </remarks>
        public Dictionary<string, string>? Headers { get; set; }

        public ICollection<Permission> Permissions { get; set; } = [];

        /// <summary>
        /// The per-user credentials held for this server; empty unless <see cref="AuthType"/> is
        /// <see cref="McpAuthTypes.UserApiKey"/>.
        /// </summary>
        public ICollection<UserMcpCredential> UserCredentials { get; set; } = [];
    }
}
