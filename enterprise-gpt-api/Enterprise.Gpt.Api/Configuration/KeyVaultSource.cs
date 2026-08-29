namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// A vault the host is to read, as resolved from the <c>KeyVault</c> section.
/// </summary>
/// <param name="VaultUri">Absolute HTTPS URI of the vault.</param>
/// <param name="ManagedIdentityClientId">Client id of the user-assigned identity, or <see langword="null"/> to let the credential chain choose.</param>
/// <param name="ReloadInterval">Polling interval, or <see langword="null"/> to read the vault once.</param>
internal sealed record KeyVaultSource(Uri VaultUri, string? ManagedIdentityClientId, TimeSpan? ReloadInterval);
