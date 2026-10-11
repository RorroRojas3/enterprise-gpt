using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Repository.Cosmos.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Repository.Cosmos.Provisioning;

/// <summary>
/// Creates the Cosmos DB database and containers the application reads and writes, and refuses to
/// start against a transcript container whose shape it cannot use.
/// </summary>
public sealed partial class CosmosResourceProvisioner(
    CosmosClient cosmosClient,
    IOptions<CosmosDbOptions> options,
    ILogger<CosmosResourceProvisioner> logger)
{
    // Never filtered, ordered or aggregated on, so indexing them only costs write RUs; _etag restores the
    // default policy's exclusion. Applies at creation only; an older container indexing more is harmless.
    private static readonly string[] ExcludedIndexPaths =
    [
        "/content/*",
        "/htmlContent/*",
        "/usage/*",
        "/feedback/*",
        "/attachments/*",
        "/\"_etag\"/?"
    ];

    private readonly CosmosClient _cosmosClient = cosmosClient;
    private readonly CosmosDbOptions _options = options.Value;
    private readonly ILogger<CosmosResourceProvisioner> _logger = logger;

    /// <summary>Ensures the database and transcript container exist and validates its partition key.</summary>
    /// <exception cref="InvalidOperationException">
    /// The transcript container exists with a different partition key path; continuing would read every
    /// transcript as empty.
    /// </exception>
    public async Task EnsureProvisionedAsync(CancellationToken cancellationToken)
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(
            _options.DatabaseId, cancellationToken: cancellationToken);

        // Never create the pre-cutover container: it would resurrect an empty one an operator already deleted.
        var transcript = await database.Database.CreateContainerIfNotExistsAsync(
            CreateTranscriptContainerProperties(_options.TranscriptContainerId),
            cancellationToken: cancellationToken);

        var actualPath = transcript.Resource.PartitionKeyPath;

        if (!string.Equals(actualPath, PartitionKeys.Path, StringComparison.Ordinal))
        {
            LogPartitionKeyMismatch(_logger, _options.TranscriptContainerId, actualPath, PartitionKeys.Path);

            throw new InvalidOperationException(
                $"Transcript container '{_options.TranscriptContainerId}' is partitioned on {actualPath}, but {PartitionKeys.Path} is required.");
        }
    }

    #region Public static methods

    /// <summary>Builds the properties the transcript container is created with.</summary>
    /// <remarks>
    /// Public so a unit test can assert the indexing policy; the Linux emulator accepts a custom policy and
    /// ignores it.
    /// </remarks>
    public static ContainerProperties CreateTranscriptContainerProperties(string containerId)
    {
        var properties = new ContainerProperties(containerId, PartitionKeys.Path)
        {
            IndexingPolicy = new IndexingPolicy
            {
                IndexingMode = IndexingMode.Consistent,
                Automatic = true
            }
        };

        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });

        foreach (var path in ExcludedIndexPaths)
        {
            properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = path });
        }

        return properties;
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "Transcript container '{ContainerId}' is partitioned on {ActualPath} but this application requires {ExpectedPath}. A container's partition key path cannot be changed; point CosmosDb:TranscriptContainerId at a new container.")]
    private static partial void LogPartitionKeyMismatch(ILogger logger, string containerId, string actualPath, string expectedPath);

    #endregion
}
