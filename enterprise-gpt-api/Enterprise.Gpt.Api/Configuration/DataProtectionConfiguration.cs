using Enterprise.Gpt.Api.Configuration.Models;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.AspNetCore.DataProtection;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Configures ASP.NET Core Data Protection, which encrypts the per-user MCP credentials this
/// application stores.
/// </summary>
internal static class DataProtectionConfiguration
{
    /// <summary>
    /// Name this application is known by within the Data Protection system.
    /// </summary>
    /// <remarks>
    /// Must never drift across deployments sharing a key ring: it is mixed into every payload, so
    /// changing it makes every stored credential unreadable and asks every user to supply theirs
    /// again.
    /// </remarks>
    private const string ApplicationName = "Enterprise.Gpt";

    #region Public static methods

    /// <summary>
    /// Persists the key ring in the application database, wrapped by a vault key.
    /// </summary>
    /// <param name="builder">The application builder whose services gain Data Protection, and whose environment decides whether an unwrapped key ring is refused.</param>
    /// <param name="keyVault">The resolved vault, or <see langword="null"/> when none is read.</param>
    /// <exception cref="InvalidOperationException">No wrapping key is configured outside development.</exception>
    /// <remarks>
    /// The database rather than blob storage: an App Service slot swap does not take the key ring
    /// with it the way the per-slot default location does.
    /// </remarks>
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

        // An unwrapped key ring lands in the same database as the credentials it protects, which
        // makes read access to that database sufficient to decrypt them — the one thing this
        // feature promises it is not. Refused rather than warned: a warning in a startup log is
        // not what should stand between a deployment and that.
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment(HostEnvironments.Testing))
        {
            throw new InvalidOperationException(
                $"{KeyVaultOptions.SectionName}:{nameof(KeyVaultOptions.DataProtectionKeyUri)} is required outside development: without it the Data Protection key ring is stored unencrypted beside the credentials it protects.");
        }
    }

    #endregion
}
