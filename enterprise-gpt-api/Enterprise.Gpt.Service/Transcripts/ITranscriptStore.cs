using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Service.Transcripts;

/// <summary>
/// One page of a transcript query.
/// </summary>
/// <typeparam name="T">The projected document type.</typeparam>
/// <param name="Items">The documents on this page, in query order.</param>
/// <param name="ContinuationToken">
/// The token that fetches the next page, or <see langword="null"/> when this is the last page.
/// </param>
public sealed record TranscriptPage<T>(IReadOnlyList<T> Items, string? ContinuationToken);

/// <summary>
/// Reads and writes the conversation transcript, one document per message under a synthetic
/// partition key holding <c>{userId}:{conversationId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IAzureCosmosService"/> rather than an extension of it because that type
/// binds one container in its constructor, and the transcript container is a different container
/// with a different partition key path.
/// </para>
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

    /// <summary>
    /// Deletes every document in a partition.
    /// </summary>
    /// <param name="partitionKey">The partition to empty.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Uses the server-side delete-by-partition-key operation only when
    /// <c>CosmosDb:UsePartitionKeyDelete</c> is set; otherwise it pages the identifiers and deletes
    /// them in batches. See that setting for why the batched path is the default.
    /// </remarks>
    Task DeletePartitionAsync(PartitionKey partitionKey, CancellationToken cancellationToken);
}
