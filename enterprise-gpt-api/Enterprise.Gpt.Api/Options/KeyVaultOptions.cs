using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>Selects and tunes the Azure Key Vault configuration source.</summary>
internal sealed class KeyVaultOptions
{
    internal const string SectionName = "KeyVault";

    /// <summary>Whether secrets are loaded from a key vault at all.</summary>
    /// <remarks>While off, no credential is constructed and no request leaves the process.</remarks>
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

    /// <summary>
    /// Identifier of the vault key that wraps the Data Protection key ring, for example
    /// <c>https://contoso.vault.azure.net/keys/data-protection</c>.
    /// </summary>
    /// <remarks>
    /// Versionless, so key rotation does not strand older payloads. Unset persists the key ring unwrapped,
    /// fit only where the database is the trust boundary (local development, the test host).
    /// </remarks>
    public string? DataProtectionKeyUri { get; set; }

    /// <summary>How often to re-read the vault, or <see langword="null"/> to read it once at startup.</summary>
    /// <remarks>
    /// <c>IOptions&lt;T&gt;</c> consumers keep their startup value. The provider swallows failed reloads
    /// without logging, so polling is visible only in the vault's own diagnostics.
    /// </remarks>
    public TimeSpan? ReloadInterval { get; set; }
}

/// <summary>
/// Rejects an enabled <see cref="KeyVaultOptions"/> section that only half-describes a vault.
/// </summary>
internal sealed class KeyVaultOptionsValidator : AbstractValidator<KeyVaultOptions>
{
    // Key Vault throttles per vault, so a faster poll buys nothing and risks a 429 storm.
    internal static readonly TimeSpan MinimumReloadInterval = TimeSpan.FromMinutes(1);

    public KeyVaultOptionsValidator()
    {
        // Blank counts as missing: a template that leaves a variable unset writes an empty app setting.
        RuleFor(options => options.VaultUri)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("KeyVault:VaultUri is required when KeyVault:Enabled is true.")
            .Must(IsHttpsUri)
            .WithMessage("KeyVault:VaultUri must be an absolute https URI, for example https://contoso.vault.azure.net/.");
        RuleFor(options => options.ReloadInterval)
            .Must(interval => interval is null || interval >= MinimumReloadInterval)
            .WithMessage($"KeyVault:ReloadInterval must be at least {MinimumReloadInterval}, or unset to read the vault once at startup.");
        RuleFor(options => options.DataProtectionKeyUri)
            .Must(uri => string.IsNullOrWhiteSpace(uri) || IsHttpsUri(uri))
            .WithMessage("KeyVault:DataProtectionKeyUri must be an absolute https key identifier, for example https://contoso.vault.azure.net/keys/data-protection.");
    }

    #region Private methods

    private static bool IsHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    #endregion
}
