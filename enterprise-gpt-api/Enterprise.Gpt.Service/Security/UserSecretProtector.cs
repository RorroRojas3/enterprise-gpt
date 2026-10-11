using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Security;

public interface IUserSecretProtector
{
    /// <summary>Encrypts a user's credential for one MCP server.</summary>
    string Protect(Guid userOid, Guid mcpServerId, string plaintext);

    /// <summary>Decrypts a stored credential, returning <see langword="false"/> rather than throwing.</summary>
    bool TryUnprotect(Guid userOid, Guid mcpServerId, string ciphertext, out string? plaintext);
}

/// <summary>
/// Protects per-user credentials with Data Protection, binding each payload to the user and server it was saved
/// for so a copied row fails to decrypt.
/// </summary>
/// <remarks>
/// Changing <c>RootPurpose</c> or losing the key ring makes every stored credential read as absent, so every user
/// is asked to supply theirs again.
/// </remarks>
public sealed partial class UserSecretProtector(IDataProtectionProvider provider, ILogger<UserSecretProtector> logger)
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
        // A revoked key, another app name or a row moved between users all mean: ask again.
        catch (CryptographicException ex)
        {
            // The string overload wraps key-ring failures as CryptographicException too; an inner
            // exception is what separates a vault outage from a bad payload.
            if (ex.InnerException is not null)
            {
                LogKeyRingCannotDecrypt(_logger, ex, mcpServerId);
            }
            else
            {
                LogCredentialUndecryptable(_logger, ex, mcpServerId);
            }

            plaintext = null;
            return false;
        }
    }

    #region Private methods

    private IDataProtector CreateProtector(Guid userOid, Guid mcpServerId)
    {
        return _provider
            .CreateProtector(RootPurpose)
            .CreateProtector(userOid.ToString("N"))
            .CreateProtector(mcpServerId.ToString("N"));
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "The Data Protection key ring could not open a stored MCP credential for server {McpServerId}; users will be asked to supply their keys again while this persists")]
    private static partial void LogKeyRingCannotDecrypt(ILogger logger, Exception exception, Guid mcpServerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored MCP credential for server {McpServerId} could not be decrypted and will be treated as absent")]
    private static partial void LogCredentialUndecryptable(ILogger logger, Exception exception, Guid mcpServerId);

    #endregion
}
