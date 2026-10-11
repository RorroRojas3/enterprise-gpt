using System.Net;
using Enterprise.Gpt.Repository.Cosmos.Models;
using Enterprise.Gpt.Repository.Cosmos.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Repository.Cosmos;

/// <summary>
/// Reads and writes the conversation transcript, one document per message under a synthetic
/// partition key holding <c>{userId}:{conversationId}</c>.
/// </summary>
/// <remarks>
/// Every member requires a partition key, so a cross-partition read (a cost and tenancy hole) is unreachable.
/// </remarks>
public interface ITranscriptStore
{
    /// <summary>
    /// Reads a single document by identifier.
    /// </summary>
    /// <returns>The document, or <see langword="null"/> when it does not exist.</returns>
    Task<T?> ReadItemAsync<T>(string id, PartitionKey partitionKey, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one page of a query against a single partition; a <see langword="null"/>
    /// <paramref name="pageSize"/> uses the configured page size.
    /// </summary>
    /// <returns>The page, carrying the token for the next one when more remain.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="partitionKey"/> is unset.</exception>
    Task<TranscriptPage<T>> QueryAsync<T>(
        QueryDefinition query,
        PartitionKey partitionKey,
        int? pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies up to 10 partial-update operations to a document without reading and rewriting it.
    /// </summary>
    /// <returns><see langword="false"/> when no document with that identifier exists.</returns>
    Task<bool> PatchItemAsync(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> operations,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs the operations <paramref name="build"/> records, in order, as one transaction against a
    /// single partition.
    /// </summary>
    /// <returns>The outcome, naming the failing operation when one failed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown before any request when the batch is empty or over <see cref="TranscriptBatch.MaxOperations"/>.
    /// </exception>
    /// <remarks>
    /// Atomicity keeps a header's counters consistent with the message documents they count.
    /// </remarks>
    Task<TranscriptBatchResult> ExecuteBatchAsync(
        PartitionKey partitionKey,
        Action<TranscriptBatch> build,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes transcript documents in the Cosmos DB container named by
/// <see cref="CosmosDbOptions.TranscriptContainerId"/>.
/// </summary>
/// <remarks>
/// A singleton over the singleton <see cref="CosmosClient"/>, so the container handle is resolved once.
/// </remarks>
public sealed class CosmosTranscriptStore(CosmosClient cosmosClient, IOptions<CosmosDbOptions> options) : ITranscriptStore
{
    /// <summary>
    /// The maximum number of operations Cosmos DB accepts in a single patch request.
    /// </summary>
    public const int MaxPatchOperations = 10;

    private readonly Container _container = cosmosClient.GetContainer(
        options.Value.DatabaseId, options.Value.TranscriptContainerId);

    private readonly CosmosDbOptions _options = options.Value;

    /// <inheritdoc />
    /// <exception cref="CosmosException">
    /// Thrown for any Cosmos DB failure other than a missing document.
    /// </exception>
    public async Task<T?> ReadItemAsync<T>(string id, PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        EnsurePartitioned(partitionKey);

        try
        {
            return await _container.ReadItemAsync<T>(id, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public async Task<TranscriptPage<T>> QueryAsync<T>(
        QueryDefinition query,
        PartitionKey partitionKey,
        int? pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsurePartitioned(partitionKey);

        var requestOptions = new QueryRequestOptions
        {
            PartitionKey = partitionKey,
            MaxItemCount = pageSize is > 0 ? pageSize : _options.PageSize
        };

        using var iterator = _container.GetItemQueryIterator<T>(query, continuationToken, requestOptions);

        if (!iterator.HasMoreResults)
        {
            return new TranscriptPage<T>([], null);
        }

        var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

        // The SDK can return a token on the final page; HasMoreResults is the authority, and passing
        // that token on would cost every caller an empty round trip.
        return new TranscriptPage<T>([.. response], iterator.HasMoreResults ? response.ContinuationToken : null);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when more than <see cref="MaxPatchOperations"/> operations are supplied.
    /// </exception>
    public async Task<bool> PatchItemAsync(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> operations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, MaxPatchOperations);
        EnsurePartitioned(partitionKey);

        // No caller reads the patched document back, so don't pay to return it.
        var requestOptions = new PatchItemRequestOptions { EnableContentResponseOnWrite = false };

        try
        {
            await _container.PatchItemAsync<dynamic>(id, partitionKey, operations, requestOptions, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<TranscriptBatchResult> ExecuteBatchAsync(
        PartitionKey partitionKey,
        Action<TranscriptBatch> build,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        EnsurePartitioned(partitionKey);

        var recorded = new TranscriptBatch();
        build(recorded);

        ArgumentOutOfRangeException.ThrowIfZero(recorded.Operations.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recorded.Operations.Count, TranscriptBatch.MaxOperations);

        var batch = _container.CreateTransactionalBatch(partitionKey);
        foreach (var operation in recorded.Operations)
        {
            operation.Apply(batch);
        }

        using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return new TranscriptBatchResult(true, response.StatusCode, null, null);
        }

        // Cosmos marks every operation that did not itself fail as 424 Failed Dependency, so the
        // one entry that is neither successful nor 424 identifies the cause.
        for (var index = 0; index < response.Count; index++)
        {
            var status = response[index].StatusCode;

            if (!IsSuccess(status) && status is not HttpStatusCode.FailedDependency)
            {
                return new TranscriptBatchResult(false, response.StatusCode, index, status);
            }
        }

        return new TranscriptBatchResult(false, response.StatusCode, null, null);
    }

    #region Private methods

    private static bool IsSuccess(HttpStatusCode status)
    {
        return (int)status is >= 200 and <= 299;
    }

    private static void EnsurePartitioned(PartitionKey partitionKey)
    {
        if (partitionKey == default || partitionKey == PartitionKey.None)
        {
            throw new ArgumentException(
                "A transcript operation requires a partition key; running one without it would fan out across every physical partition.",
                nameof(partitionKey));
        }
    }

    #endregion

}
