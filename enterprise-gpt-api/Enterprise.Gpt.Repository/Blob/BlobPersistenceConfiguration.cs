using Azure.Storage.Blobs;
using Enterprise.Gpt.Repository.Blob.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Repository.Blob;

/// <summary>
/// Registers the blob store that holds document files.
/// </summary>
public static class BlobPersistenceConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the storage options, the account client and the <see cref="IDocumentStore"/> gateway.
    /// </summary>
    public static IServiceCollection AddEnterpriseGptBlobPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BlobStorageOptions>()
            .Bind(configuration.GetSection(BlobStorageOptions.SectionName));

        services.AddSingleton(services =>
            new BlobServiceClient(services.GetRequiredService<IOptions<BlobStorageOptions>>().Value.ConnectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDocumentStore, BlobDocumentStore>();

        return services;
    }

    #endregion
}
