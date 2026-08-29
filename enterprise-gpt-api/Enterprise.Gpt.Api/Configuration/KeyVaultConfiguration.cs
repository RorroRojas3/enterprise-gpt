using Azure.Core;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Adds Azure Key Vault to the configuration chain when the <c>KeyVault</c> section enables it.
/// </summary>
/// <remarks>
/// The source is appended last, so a vault secret outranks the same key from an environment variable
/// or <c>appsettings.json</c>. A deployment that also resolves App Service Key Vault references into
/// app settings would therefore be reading one key through two mechanisms.
/// </remarks>
internal static class KeyVaultConfiguration
{
    /// <summary>Environment name the integration host runs under, spelled the same way in <c>Program.cs</c>.</summary>
    private const string TestingEnvironmentName = "Testing";

    // Key Vault throttles per vault, so a faster poll buys nothing and risks a 429 storm.
    private static readonly TimeSpan MinimumReloadInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reads the vault into configuration, and does nothing at all when the section leaves it off.
    /// </summary>
    /// <returns>The vault that was added, or <see langword="null"/> when none was.</returns>
    /// <remarks>
    /// Must run before anything reads configuration: <c>builder.Configuration</c> rebuilds on every
    /// source added, so the vault is read here and an unreachable one fails the host immediately.
    /// </remarks>
    internal static KeyVaultSource? AddEnterpriseKeyVault(this WebApplicationBuilder builder)
    {
        var source = Resolve(builder.Configuration, builder.Environment);

        if (source is null)
        {
            return null;
        }

        var credentialOptions = new DefaultAzureCredentialOptions();

        if (source.ManagedIdentityClientId is not null)
        {
            credentialOptions.ManagedIdentityClientId = source.ManagedIdentityClientId;
            // Set on both legs: the workload-identity credential reads its own property and would
            // otherwise fall back to AZURE_CLIENT_ID, ignoring the identity configured here.
            credentialOptions.WorkloadIdentityClientId = source.ManagedIdentityClientId;
        }

        // Wider retry per Microsoft's throttling guidance, but with a per-attempt timeout: the 100s
        // default would let a black-holed vault outlive the platform's startup probe, replacing the
        // exception that explains the failure with a bare "container did not start".
        var clientOptions = new SecretClientOptions
        {
            Retry =
            {
                Mode = RetryMode.Exponential,
                MaxRetries = 5,
                Delay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromSeconds(16),
                NetworkTimeout = TimeSpan.FromSeconds(10)
            }
        };

        var client = new SecretClient(
            source.VaultUri,
            new DefaultAzureCredential(credentialOptions),
            clientOptions);

        builder.Configuration.AddAzureKeyVault(
            client,
            new AzureKeyVaultConfigurationOptions { ReloadInterval = source.ReloadInterval });

        return source;
    }

    /// <summary>
    /// Decides whether a vault should be read, and rejects a section that half-describes one.
    /// </summary>
    /// <param name="environment">The host environment, so a test host never reaches a vault.</param>
    /// <returns>The validated vault, or <see langword="null"/> when none is to be read.</returns>
    /// <exception cref="InvalidOperationException">The section enables a vault it does not describe.</exception>
    internal static KeyVaultSource? Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(KeyVaultOptions.SectionName).Get<KeyVaultOptions>();

        if (options is not { Enabled: true } || environment.IsEnvironment(TestingEnvironmentName))
        {
            return null;
        }

        // Enabled with nothing to point at is a deployment mistake rather than a request to stay
        // local: every secret it expects to find would silently be missing. Blank counts, because a
        // template that leaves a variable unset writes an empty app setting rather than no setting.
        if (string.IsNullOrWhiteSpace(options.VaultUri))
        {
            throw new InvalidOperationException(
                $"{KeyVaultOptions.SectionName}:{nameof(KeyVaultOptions.VaultUri)} is required when {KeyVaultOptions.SectionName}:{nameof(KeyVaultOptions.Enabled)} is true.");
        }

        if (!Uri.TryCreate(options.VaultUri, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{KeyVaultOptions.SectionName}:{nameof(KeyVaultOptions.VaultUri)} must be an absolute https URI, for example https://contoso.vault.azure.net/.");
        }

        if (options.ReloadInterval is { } interval && interval < MinimumReloadInterval)
        {
            throw new InvalidOperationException(
                $"{KeyVaultOptions.SectionName}:{nameof(KeyVaultOptions.ReloadInterval)} must be at least {MinimumReloadInterval}, or unset to read the vault once at startup.");
        }

        return new KeyVaultSource(
            vaultUri,
            string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) ? null : options.ManagedIdentityClientId,
            options.ReloadInterval);
    }

    /// <summary>
    /// Reports that the vault is in the chain, so an operator can tell a resolved secret from an
    /// environment variable that happened to carry the same key.
    /// </summary>
    /// <param name="source">The result of <see cref="AddEnterpriseKeyVault"/>; <see langword="null"/> reports nothing.</param>
    internal static void LogStatus(KeyVaultSource? source, ILogger logger)
    {
        if (source is null)
        {
            return;
        }

        // The host, never the secret names: which secrets a deployment holds is itself worth not
        // publishing to a log sink.
        logger.LogInformation(
            "Configuration is reading secrets from key vault {VaultHost} (user-assigned identity: {UsesUserAssignedIdentity}, reload interval: {ReloadInterval})",
            source.VaultUri.Host,
            source.ManagedIdentityClientId is not null,
            source.ReloadInterval);
    }
}
