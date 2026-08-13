using Enterprise.Gpt.Service.Settings;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Enterprise.Gpt.Service;

/// <summary>
/// Creates and verifies the transcript container.
/// </summary>
/// <remarks>
/// Shared by application startup and the integration-test fixture so both provision an identical
/// container — a fixture that built its own shape would prove nothing about production.
/// </remarks>
public static class CosmosContainerBootstrap
{
    /// <summary>
    /// The partition key paths of the transcript container, in order.
    /// </summary>
    /// <remarks>
    /// Hierarchical rather than the single <c>/userId</c> the retired container used. This earns
    /// its place three ways: a transcript read is prefix-routed to one subpartition instead of
    /// filtered inside a whole user's partition, the 20 GB logical-partition ceiling applies per
    /// conversation rather than per user, and purging a conversation becomes addressable by a
    /// complete partition key.
    /// </remarks>
    public static readonly IReadOnlyList<string> PartitionKeyPaths = ["/userId", "/conversationId"];

    /// <summary>
    /// Creates the database and transcript container when absent, and verifies an existing
    /// container's partition key paths.
    /// </summary>
    /// <param name="cosmosClient">The client to provision through.</param>
    /// <param name="options">The validated Cosmos settings.</param>
    /// <param name="logger">Receives the provisioning outcome and any drift.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous provisioning operation.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured container exists with different partition key paths, because
    /// this application cannot address documents in it.
    /// </exception>
    public static async Task EnsureTranscriptContainerAsync(
        CosmosClient cosmosClient,
        CosmosOptions options,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var database = await cosmosClient
            .CreateDatabaseIfNotExistsAsync(options.DatabaseId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var container = database.Database.GetContainer(options.TranscriptContainerId);

        ContainerProperties? existing = null;
        try
        {
            var response = await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            existing = response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Falls through to creation. Read-then-create rather than CreateContainerIfNotExists,
            // which returns an existing container without checking that its key paths match.
        }

        if (existing is null)
        {
            await database.Database
                .CreateContainerAsync(BuildContainerProperties(options.TranscriptContainerId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Created Cosmos transcript container {ContainerId} partitioned on {PartitionKeyPaths}",
                options.TranscriptContainerId,
                string.Join(", ", PartitionKeyPaths));

            return;
        }

        var actualPaths = existing.PartitionKeyPaths;
        if (!actualPaths.SequenceEqual(PartitionKeyPaths))
        {
            logger.LogError(
                "Cosmos container {ContainerId} is partitioned on {ActualPartitionKeyPaths} but this application requires {ExpectedPartitionKeyPaths}",
                options.TranscriptContainerId,
                string.Join(", ", actualPaths),
                string.Join(", ", PartitionKeyPaths));

            throw new InvalidOperationException(
                $"Cosmos container '{options.TranscriptContainerId}' is partitioned on [{string.Join(", ", actualPaths)}] "
                + $"but this application requires [{string.Join(", ", PartitionKeyPaths)}]. "
                + "Configure CosmosDb:TranscriptContainerId with a container that has the required partition key paths.");
        }

        await ConvergeIndexingPolicyAsync(container, existing, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the properties a new transcript container is created with.
    /// </summary>
    /// <param name="containerId">The container identifier.</param>
    /// <returns>The container properties, including the indexing policy.</returns>
    public static ContainerProperties BuildContainerProperties(string containerId)
    {
        var properties = new ContainerProperties(containerId, PartitionKeyPaths.ToList())
        {
            PartitionKeyDefinitionVersion = PartitionKeyDefinitionVersion.V2
        };

        properties.IndexingPolicy.IncludedPaths.Clear();
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });

        // Message bodies are never filtered on and are the bulk of a document's bytes, so indexing
        // them is pure write RU and storage. Everything a query does touch — sequence, type,
        // conversationId, dateCreated — stays indexed under the wildcard above.
        properties.IndexingPolicy.ExcludedPaths.Clear();
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/content/*" });
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/htmlContent/*" });

        return properties;
    }

    /// <summary>
    /// Brings an existing container's indexing policy in line with
    /// <see cref="BuildContainerProperties"/>, logging rather than failing when it cannot.
    /// </summary>
    /// <remarks>
    /// Never fails startup: indexing drift costs request units, not correctness, and some backends
    /// — the Linux emulator among them — accept a custom policy without applying it, which would
    /// otherwise make every local run unstartable.
    /// </remarks>
    private static async Task ConvergeIndexingPolicyAsync(
        Container container,
        ContainerProperties existing,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var expected = BuildContainerProperties(existing.Id);
        var actualExcluded = existing.IndexingPolicy.ExcludedPaths.Select(path => path.Path).ToHashSet(StringComparer.Ordinal);
        var expectedExcluded = expected.IndexingPolicy.ExcludedPaths.Select(path => path.Path);

        if (expectedExcluded.All(actualExcluded.Contains))
        {
            return;
        }

        try
        {
            existing.IndexingPolicy = expected.IndexingPolicy;
            await container.ReplaceContainerAsync(existing, cancellationToken: cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Updated the indexing policy of Cosmos container {ContainerId} to exclude message bodies",
                existing.Id);
        }
        catch (CosmosException ex)
        {
            logger.LogWarning(
                ex,
                "Could not update the indexing policy of Cosmos container {ContainerId}; message bodies stay indexed, which costs request units but not correctness",
                existing.Id);
        }
    }
}
