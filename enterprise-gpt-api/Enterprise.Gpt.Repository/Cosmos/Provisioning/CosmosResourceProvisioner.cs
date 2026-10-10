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
    /// <summary>
    /// The document properties excluded from the transcript container's index.
    /// </summary>
    /// <remarks>
    /// <c>content</c> and <c>htmlContent</c> hold the prompt and the answer and are the bulk of a
    /// message document's bytes; <c>usage</c> is a per-turn token block; <c>feedback</c> is a
    /// rating, and reporting reads the relational projection rather than this container. None of
    /// the four is ever filtered, ordered or aggregated on, so indexing them buys nothing and is
    /// charged on every write. The <c>_etag</c> exclusion restores the behaviour the default policy
    /// has and a custom policy otherwise drops.
    /// <para>
    /// This list governs container <em>creation</em> only: <c>CreateContainerIfNotExistsAsync</c>
    /// leaves an existing container's policy alone, so an account provisioned before a path was
    /// added here keeps indexing it. That costs a little write throughput and nothing else, which
    /// is why no operator step exists to reconcile it.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Ensures the database and the transcript container exist, and validates the container's
    /// partition key path.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the transcript container already exists with a different partition key path.
    /// A container's key path is fixed at creation, so continuing would write documents whose
    /// partition no read path can address — which surfaces as an empty transcript rather than an
    /// error, the one failure mode this must not have.
    /// </exception>
    public async Task EnsureProvisionedAsync(CancellationToken cancellationToken)
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(
            _options.DatabaseId, cancellationToken: cancellationToken);

        // Only the transcript container. The pre-cutover container is deliberately never opened:
        // nothing reads or writes it, and creating it would resurrect an empty one on any account
        // where an operator has already deleted it.
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

    /// <summary>
    /// Builds the properties the transcript container is created with.
    /// </summary>
    /// <param name="containerId">The container's identifier.</param>
    /// <returns>The container definition, partitioned on <see cref="PartitionKeys.Path"/>.</returns>
    /// <remarks>
    /// Pure and public so the indexing policy can be asserted by a unit test. It cannot be asserted
    /// against the Linux emulator: that emulator accepts a custom indexing policy and then ignores
    /// it, so a read-back there would prove nothing about production.
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
