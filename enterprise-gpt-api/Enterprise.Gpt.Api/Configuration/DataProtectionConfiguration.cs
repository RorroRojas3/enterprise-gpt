using Enterprise.Gpt.Api.Configuration.Models;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.AspNetCore.DataProtection;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>Data Protection encrypts the per-user MCP credentials this application stores.</summary>
internal static class DataProtectionConfiguration
{
    // Must never drift across deployments sharing a key ring: it is mixed into every payload, so changing it
    // makes every stored credential unreadable.
    private const string ApplicationName = "Enterprise.Gpt";

    #region Public static methods

    /// <summary>Persists the key ring in the application database, wrapped by a vault key.</summary>
    /// <exception cref="InvalidOperationException">No wrapping key is configured outside development.</exception>
    /// <remarks>Not the per-slot default location, which an App Service slot swap leaves behind.</remarks>
    internal static void AddEnterpriseGptDataProtection(this WebApplicationBuilder builder, KeyVaultSource? keyVault)
    {
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<EnterpriseGptDbContext>();

        if (keyVault?.DataProtectionKeyUri is { } keyUri)
        {
            dataProtection.ProtectKeysWithAzureKeyVault(keyUri, KeyVaultConfiguration.CreateCredential(keyVault));

            return;
        }

        // Unwrapped, the key ring sits in the database beside the credentials it protects, so read access to
        // that database decrypts them. Refused rather than warned.
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment(HostEnvironments.Testing))
        {
            throw new InvalidOperationException(
                $"{KeyVaultOptions.SectionName}:{nameof(KeyVaultOptions.DataProtectionKeyUri)} is required outside development: without it the Data Protection key ring is stored unencrypted beside the credentials it protects.");
        }
    }

    #endregion
}
