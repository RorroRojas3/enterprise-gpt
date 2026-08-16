using System.Net;
using System.Text.Json.Serialization;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Transcripts;

/// <summary>
/// Reads and writes transcript documents in the Cosmos DB container named by
/// <see cref="CosmosOptions.TranscriptContainerId"/>.
/// </summary>
/// <remarks>
/// Registered as a singleton over the singleton <see cref="CosmosClient"/>. The container handle
/// is resolved once: <c>GetContainer</c> is a local lookup that issues no request, so holding it
/// costs nothing and avoids repeating the resolution on every turn.
/// </remarks>
/// <param name="cosmosClient">The account client.</param>
/// <param name="options">The bound and validated Cosmos settings.</param>
/// <param name="logger">The logger.</param>
public sealed class TranscriptStore(
    CosmosClient cosmosClient,
    IOptions<CosmosOptions> options,
    ILogger<TranscriptStore> logger) : ITranscriptStore
{
    /// <summary>
    /// The maximum number of operations Cosmos DB accepts in a single patch request.
    /// </summary>
    public const int MaxPatchOperations = 10;

    private readonly Container _container = cosmosClient.GetContainer(
        options.Value.DatabaseId, options.Value.TranscriptContainerId);

    private readonly CosmosOptions _options = options.Value;
    private readonly ILogger<TranscriptStore> _logger = logger;

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

        // The patched document is a header carrying counters and a name; nothing reads it back, and
        // returning it on every rename and every turn is bandwidth spent on a discarded value.
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

    /// <inheritdoc />
    public async Task DeletePartitionAsync(PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        EnsurePartitioned(partitionKey);

        if (_options.UsePartitionKeyDelete)
        {
            using var response = await _container
                .DeleteAllItemsByPartitionKeyStreamAsync(partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // Falling through rather than throwing: the capability is enabled per account and the
            // only way to discover it is missing is to be refused, so a deployment that turned the
            // setting on optimistically still purges rather than leaving the documents behind.
            _logger.LogWarning(
                "Delete-by-partition-key returned {StatusCode}; falling back to batched deletes. Confirm the DeleteAllItemsByPartitionKey capability is enabled on the account, or clear CosmosDb:UsePartitionKeyDelete.",
                (int)response.StatusCode);
        }

        await DeletePartitionInBatchesAsync(partitionKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeletePartitionInBatchesAsync(PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT c.id FROM c");

        while (true)
        {
            // Drained rather than read once. A page size is a ceiling Cosmos may return fewer than,
            // and a page can come back empty with more results behind it, so a single read proves
            // neither that there is work nor that there is none.
            var ids = await DrainIdsAsync(query, partitionKey, cancellationToken).ConfigureAwait(false);

            if (ids.Count == 0)
            {
                return;
            }

            // Re-read from the start on the next pass rather than following a continuation token:
            // the documents the token described no longer exist.
            foreach (var chunk in ids.Chunk(TranscriptBatch.MaxOperations))
            {
                var result = await ExecuteBatchAsync(partitionKey, batch =>
                {
                    foreach (var id in chunk)
                    {
                        batch.Delete(id);
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Purging a conversation's transcript failed: {result.Describe()}.");
                }
            }
        }
    }

    /// <summary>
    /// Reads every document identifier in a partition, following continuation tokens to the end.
    /// </summary>
    private async Task<List<string>> DrainIdsAsync(
        QueryDefinition query, PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        List<string> ids = [];
        string? continuationToken = null;

        do
        {
            var page = await QueryAsync<DocumentId>(
                query, partitionKey, _options.PageSize, continuationToken, cancellationToken)
                .ConfigureAwait(false);

            ids.AddRange(page.Items.Select(x => x.Id));
            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        return ids;
    }

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

    /// <summary>
    /// Projection for queries that only need identifiers.
    /// </summary>
    private sealed record DocumentId
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = null!;
    }
}
