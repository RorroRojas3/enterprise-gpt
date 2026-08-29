namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Selects and tunes the Azure Key Vault configuration source, bound from the <c>KeyVault</c> section.
/// </summary>
internal sealed class KeyVaultOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    internal const string SectionName = "KeyVault";

    /// <summary>Whether secrets are loaded from a key vault at all.</summary>
    /// <remarks>
    /// Off ships as the default, and nothing about the vault is touched while it stays off: no
    /// credential is constructed and no request leaves the process.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>Vault URI, for example <c>https://contoso.vault.azure.net/</c>.</summary>
    /// <remarks>Required once <see cref="Enabled"/> is set; a half-configured section fails startup.</remarks>
    public string? VaultUri { get; set; }

    /// <summary>Client id of the user-assigned managed identity to authenticate with.</summary>
    /// <remarks>
    /// Leave unset for a system-assigned identity or for local development, where the credential chain
    /// picks up the signed-in Azure CLI or Visual Studio account instead.
    /// </remarks>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>How often to re-read the vault, or <see langword="null"/> to read it once at startup.</summary>
    /// <remarks>
    /// A reload reaches only code that re-reads configuration or watches a change token; anything bound
    /// through <c>IOptions&lt;T&gt;</c> keeps the value it started with. The provider swallows a failed
    /// reload and logs nothing, so polling is only observable through the vault's own diagnostics.
    /// </remarks>
    public TimeSpan? ReloadInterval { get; set; }
}
