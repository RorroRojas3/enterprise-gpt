using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// One user's own bearer credential for an MCP server registered with
/// <see cref="Common.Enums.McpAuthTypes.UserApiKey"/>, held encrypted at rest.
/// </summary>
[Table(nameof(UserMcpCredential), Schema = "Core")]
public class UserMcpCredential : BaseModifiedByEntity
{
    [ForeignKey(nameof(User))]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(McpServer))]
    public Guid McpServerId { get; set; }

    /// <summary>
    /// The credential protected by <c>IUserSecretProtector</c>, whose purpose chain binds it
    /// to <see cref="UserId"/> and <see cref="McpServerId"/>.
    /// </summary>
    /// <remarks>
    /// Never leaves the service layer: no DTO carries it and no route returns it. A payload
    /// the current key ring cannot open reads as "no credential", not as an error.
    /// </remarks>
    [StringLength(2048)]
    public string Ciphertext { get; set; } = null!;

    /// <summary>
    /// Last four characters of the credential, so a user can tell which key is stored.
    /// </summary>
    [StringLength(4)]
    public string ApiKeyHint { get; set; } = null!;

    /// <summary>
    /// When the MCP server last refused this credential, or <see langword="null"/> while it
    /// is believed good. Cleared whenever the user saves a new one.
    /// </summary>
    /// <remarks>
    /// A rejected credential is treated as absent, so the user is asked for a new one rather
    /// than being shown a retryable "server unavailable" for a fault only they can fix.
    /// </remarks>
    public DateTimeOffset? DateRejected { get; set; }

    public User User { get; set; } = null!;

    public McpServer McpServer { get; set; } = null!;
}
