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
/// <para>
/// Every member takes a partition key and none accepts a cross-partition query. That is the point:
/// a transcript read that fanned out across partitions would be both a cost surprise and a tenancy
/// hole, so it is unreachable through this surface rather than merely discouraged.
/// </para>
/// </remarks>
public interface ITranscriptStore
{
    /// <summary>
    /// Reads a single document by identifier.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="id">The document's identifier.</param>
    /// <param name="partitionKey">The partition the document lives in.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>The document, or <see langword="null"/> when it does not exist.</returns>
    Task<T?> ReadItemAsync<T>(string id, PartitionKey partitionKey, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one page of a query against a single partition.
    /// </summary>
    /// <typeparam name="T">The projected document type.</typeparam>
    /// <param name="query">The parameterized query to run.</param>
    /// <param name="partitionKey">The partition to query. Required.</param>
    /// <param name="pageSize">
    /// How many documents to request, or <see langword="null"/> to use the configured page size.
    /// </param>
    /// <param name="continuationToken">
    /// The token returned by the previous page, or <see langword="null"/> to start at the first.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>The page, carrying the token for the next one when more remain.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="partitionKey"/> is unset, because the query would otherwise fan
    /// out across every physical partition.
    /// </exception>
    Task<TranscriptPage<T>> QueryAsync<T>(
        QueryDefinition query,
        PartitionKey partitionKey,
        int? pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies partial-update operations to a document without reading and rewriting it.
    /// </summary>
    /// <param name="id">The identifier of the document to patch.</param>
    /// <param name="partitionKey">The partition the document lives in.</param>
    /// <param name="operations">The updates to apply. Cosmos DB permits at most 10 per call.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>
    /// <see langword="true"/> when the document was patched, <see langword="false"/> when no
    /// document with that identifier exists.
    /// </returns>
    Task<bool> PatchItemAsync(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> operations,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a set of operations as one transaction against a single partition.
    /// </summary>
    /// <param name="partitionKey">The partition every operation acts on.</param>
    /// <param name="build">Records the operations to run, in order.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>The outcome, naming the failing operation when one failed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the batch is empty or exceeds <see cref="TranscriptBatch.MaxOperations"/>, so an
    /// oversized batch fails before the request leaves the process rather than as a 400.
    /// </exception>
    /// <remarks>
    /// Atomicity within the partition is what keeps a header's counters honest: the message
    /// documents and the counter patch that accounts for them either all land or none do, so the
    /// stored count cannot drift from the documents backing it.
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
/// Registered as a singleton over the singleton <see cref="CosmosClient"/>. The container handle
/// is resolved once: <c>GetContainer</c> is a local lookup that issues no request, so holding it
/// costs nothing and avoids repeating the resolution on every turn.
/// </remarks>
/// <param name="cosmosClient">The account client.</param>
/// <param name="options">The bound and validated Cosmos settings.</param>
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

        // The SDK reports a continuation token on the final page too when the backend cannot yet
        // prove the page was the last; HasMoreResults is the authority, so the token is dropped
        // when it says otherwise. Returning it would cost every caller one empty round trip.
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

        // No caller reads the patched document back — a header's counters and name, a message's
        // rating — so returning it on every rename, every turn and every thumb is bandwidth spent
        // on a value that is discarded.
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
