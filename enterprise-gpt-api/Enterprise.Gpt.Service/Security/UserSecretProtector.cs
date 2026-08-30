using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Enterprise.Gpt.Service.Security;

public interface IUserSecretProtector
{
    /// <summary>
    /// Encrypts a user's credential for one MCP server.
    /// </summary>
    /// <param name="userOid">The owning user's Entra object id.</param>
    /// <param name="mcpServerId">The server the credential is for.</param>
    /// <param name="plaintext">The credential as the user supplied it.</param>
    /// <returns>The protected payload to store.</returns>
    string Protect(Guid userOid, Guid mcpServerId, string plaintext);

    /// <summary>
    /// Decrypts a stored credential, reporting failure rather than throwing.
    /// </summary>
    /// <param name="userOid">The user the stored row belongs to.</param>
    /// <param name="mcpServerId">The server the stored row is for.</param>
    /// <param name="ciphertext">The stored payload.</param>
    /// <param name="plaintext">The recovered credential, or <see langword="null"/> on failure.</param>
    /// <returns><see langword="false"/> when the payload cannot be opened.</returns>
    bool TryUnprotect(Guid userOid, Guid mcpServerId, string ciphertext, out string? plaintext);
}

/// <summary>
/// Protects per-user credentials with ASP.NET Core Data Protection, binding each payload to the
/// user and server it was saved for.
/// </summary>
/// <remarks>
/// The ids are chained into the protector's purpose, so a row copied to another user or another
/// server fails to decrypt instead of working. Changing the root purpose string, or losing the
/// key ring, makes every stored credential unreadable — recoverably, since a payload that will
/// not open is treated as "no credential", but every user is then asked to supply theirs again.
/// </remarks>
public sealed class UserSecretProtector(IDataProtectionProvider provider, ILogger<UserSecretProtector> logger)
    : IUserSecretProtector
{
    private const string RootPurpose = "Enterprise.Gpt.McpUserCredential.v1";

    private readonly IDataProtectionProvider _provider = provider;
    private readonly ILogger<UserSecretProtector> _logger = logger;

    /// <inheritdoc />
    public string Protect(Guid userOid, Guid mcpServerId, string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        return CreateProtector(userOid, mcpServerId).Protect(plaintext);
    }

    /// <inheritdoc />
    public bool TryUnprotect(Guid userOid, Guid mcpServerId, string ciphertext, out string? plaintext)
    {
        try
        {
            plaintext = CreateProtector(userOid, mcpServerId).Unprotect(ciphertext);
            return true;
        }
        // A revoked key, a payload written under another application name and a row moved
        // between users are one answer to the caller: ask for the credential again.
        catch (CryptographicException ex)
        {
            // The string overload rewraps everything as CryptographicException, key-ring
            // resolution included — so a vault outage arrives here looking like a bad payload
            // and would otherwise be one warning per user per turn with nothing naming the
            // cause. An inner exception is what tells the two apart.
            if (ex.InnerException is not null)
            {
                _logger.LogError(ex,
                    "The Data Protection key ring could not open a stored MCP credential for server {McpServerId}; users will be asked to supply their keys again while this persists",
                    mcpServerId);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Stored MCP credential for server {McpServerId} could not be decrypted and will be treated as absent",
                    mcpServerId);
            }

            plaintext = null;
            return false;
        }
    }

    private IDataProtector CreateProtector(Guid userOid, Guid mcpServerId)
    {
        return _provider
            .CreateProtector(RootPurpose)
            .CreateProtector(userOid.ToString("N"))
            .CreateProtector(mcpServerId.ToString("N"));
    }
}
