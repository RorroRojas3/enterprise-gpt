using Azure.Core;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Enterprise.Gpt.Api.Configuration.Models;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Appends Azure Key Vault last, so a vault secret outranks the same key from an environment variable (App
/// Service Key Vault references included) or <c>appsettings.json</c>.
/// </summary>
internal static partial class KeyVaultConfiguration
{
    #region Public static methods

    /// <summary>
    /// Reads the vault into configuration; returns <see langword="null"/> when the section leaves it off.
    /// </summary>
    /// <remarks>
    /// Must run before anything reads configuration: <c>builder.Configuration</c> rebuilds on every source
    /// added, so an unreachable vault fails the host here.
    /// </remarks>
    internal static KeyVaultSource? AddEnterpriseGptKeyVault(this WebApplicationBuilder builder)
    {
        var source = Resolve(builder.Configuration, builder.Environment);

        if (source is null)
        {
            return null;
        }

        // Wider retry per Microsoft's throttling guidance, with a per-attempt timeout: the 100s default lets
        // a black-holed vault outlive the startup probe, leaving only "container did not start".
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

        var client = new SecretClient(source.VaultUri, CreateCredential(source), clientOptions);

        builder.Configuration.AddAzureKeyVault(
            client,
            new AzureKeyVaultConfigurationOptions { ReloadInterval = source.ReloadInterval });

        return source;
    }

    /// <summary>
    /// Shared with the Data Protection registration so the key ring cannot silently authenticate as a different
    /// principal than the one the secrets were granted to.
    /// </summary>
    internal static DefaultAzureCredential CreateCredential(KeyVaultSource source)
    {
        var credentialOptions = new DefaultAzureCredentialOptions();

        if (source.ManagedIdentityClientId is not null)
        {
            credentialOptions.ManagedIdentityClientId = source.ManagedIdentityClientId;
            // Set on both legs: the workload-identity credential reads its own property and would
            // otherwise fall back to AZURE_CLIENT_ID, ignoring the identity configured here.
            credentialOptions.WorkloadIdentityClientId = source.ManagedIdentityClientId;
        }

        return new DefaultAzureCredential(credentialOptions);
    }

    /// <summary>
    /// Decides whether a vault should be read, and rejects a section that half-describes one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The section enables a vault it does not describe.</exception>
    internal static KeyVaultSource? Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(KeyVaultOptions.SectionName).Get<KeyVaultOptions>();

        if (options is not { Enabled: true } || environment.IsEnvironment(HostEnvironments.Testing))
        {
            return null;
        }

        // Validated by hand rather than through IOptions: this runs before the host exists, because the
        // vault it describes is itself a configuration source.
        var result = new KeyVaultOptionsValidator().Validate(options);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(result.Errors[0].ErrorMessage);
        }

        return new KeyVaultSource(
            new Uri(options.VaultUri!),
            string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) ? null : options.ManagedIdentityClientId,
            options.ReloadInterval,
            string.IsNullOrWhiteSpace(options.DataProtectionKeyUri) ? null : new Uri(options.DataProtectionKeyUri));
    }

    /// <summary>
    /// Reports that the vault is in the chain, so an operator can tell a resolved secret from an
    /// environment variable.
    /// </summary>
    /// <remarks>Takes the built application's logger: the vault is added before the host exists.</remarks>
    internal static void LogStatus(KeyVaultSource? source, ILogger logger)
    {
        if (source is null)
        {
            return;
        }

        // The host, never the secret names: which secrets a deployment holds does not belong in a log sink.
        LogReadingKeyVaultSecrets(
            logger, source.VaultUri.Host, source.ManagedIdentityClientId is not null, source.ReloadInterval);
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration is reading secrets from key vault {VaultHost} (user-assigned identity: {UsesUserAssignedIdentity}, reload interval: {ReloadInterval})")]
    private static partial void LogReadingKeyVaultSecrets(
        ILogger logger, string vaultHost, bool usesUserAssignedIdentity, TimeSpan? reloadInterval);

    #endregion
}
