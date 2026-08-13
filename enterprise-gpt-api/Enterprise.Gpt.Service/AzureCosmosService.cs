using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System.Net;

/// <summary>
/// Contains services for Azure Cosmos DB operations.
/// </summary>
namespace Enterprise.Gpt.Service
{
    /// <summary>
    /// Defines operations against the transcript container.
    /// </summary>
    /// <remarks>
    /// Every member takes the conversation's owning user and its identifier rather than a
    /// pre-built partition key. That is deliberate: the container is partitioned hierarchically on
    /// <c>/userId</c> then <c>/conversationId</c>, and taking both levels as parameters makes a
    /// cross-partition query and a partial-key purge impossible to express, rather than mistakes
    /// caught at runtime.
    /// </remarks>
    public interface IAzureCosmosService
    {
        /// <summary>
        /// Reads a single document by identifier.
        /// </summary>
        /// <typeparam name="T">The document type.</typeparam>
        /// <param name="id">The document identifier.</param>
        /// <param name="userId">The owning user, the first partition key level.</param>
        /// <param name="conversationId">The conversation, the second partition key level.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>The document, or <see langword="null"/> when no document with that identifier exists.</returns>
        Task<T?> GetItemAsync<T>(string id, Guid userId, Guid conversationId, CancellationToken cancellationToken);

        /// <summary>
        /// Applies partial-update operations to a document and returns the result.
        /// </summary>
        /// <typeparam name="T">The document type.</typeparam>
        /// <param name="id">The document identifier.</param>
        /// <param name="userId">The owning user, the first partition key level.</param>
        /// <param name="conversationId">The conversation, the second partition key level.</param>
        /// <param name="operations">The patch operations to apply, at most ten.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>The patched document, or <see langword="null"/> when no document with that identifier exists.</returns>
        /// <remarks>
        /// Returns the patched document because callers need values the server computed — a
        /// counter incremented under concurrency reads back as the value this caller was allocated,
        /// which is what makes it usable as a sequence allocator.
        /// </remarks>
        /// <example>
        /// <code language="csharp">
        /// var header = await cosmosService.PatchItemAsync&lt;CosmosConversationHeader&gt;(
        ///     conversationId.ToString(), userId, conversationId,
        ///     [PatchOperation.Increment("/messageCount", 2)], cancellationToken);
        /// </code>
        /// </example>
        Task<T?> PatchItemAsync<T>(string id, Guid userId, Guid conversationId, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken);

        /// <summary>
        /// Runs a query against one conversation's documents and returns a single page.
        /// </summary>
        /// <typeparam name="T">The type the query projects to.</typeparam>
        /// <param name="query">The query, with its parameters bound.</param>
        /// <param name="userId">The owning user, the first partition key level.</param>
        /// <param name="conversationId">The conversation, the second partition key level.</param>
        /// <param name="maxItemCount">The maximum number of items in the page. Must be positive.</param>
        /// <param name="continuationToken">The token from the previous page, or <see langword="null"/> for the first.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>One page of results and the token for the next, if any.</returns>
        Task<CosmosPage<T>> QueryPageAsync<T>(QueryDefinition query, Guid userId, Guid conversationId, int maxItemCount, string? continuationToken, CancellationToken cancellationToken);

        /// <summary>
        /// Executes operations as a single transaction scoped to one conversation.
        /// </summary>
        /// <param name="userId">The owning user, the first partition key level.</param>
        /// <param name="conversationId">The conversation, the second partition key level.</param>
        /// <param name="operations">The operations to execute, at most one hundred.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="CosmosBatchFailedException">
        /// Thrown when the batch is rejected. Nothing in it was written.
        /// </exception>
        Task ExecuteBatchAsync(Guid userId, Guid conversationId, IReadOnlyList<CosmosBatchOperation> operations, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes every document belonging to one conversation.
        /// </summary>
        /// <param name="userId">The owning user, the first partition key level.</param>
        /// <param name="conversationId">The conversation, the second partition key level.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>The number of documents deleted.</returns>
        /// <remarks>
        /// Both partition key levels are required. A partial hierarchical key does not address a
        /// complete logical partition, so a purge given only a user would either fail at the
        /// service or, worse, be interpreted more broadly than intended.
        /// </remarks>
        Task<int> PurgeConversationAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

        /// <summary>
        /// Reads a single document from a container partitioned on <c>/userId</c> alone.
        /// </summary>
        /// <typeparam name="T">The document type.</typeparam>
        /// <param name="id">The document identifier.</param>
        /// <param name="partitionKey">The single-level partition key value.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>The document, or <see langword="null"/> when it does not exist.</returns>
        /// <remarks>
        /// Serves the one-document-per-conversation transcript that the per-message shape replaces.
        /// Removed once the write and read paths move onto the new container.
        /// </remarks>
        [Obsolete("Single-level partition key. Use the (userId, conversationId) overload; removed when the per-message transcript lands.")]
        Task<T?> GetItemAsync<T>(string id, string partitionKey, CancellationToken cancellationToken);

        /// <summary>
        /// Creates a document in a container partitioned on <c>/userId</c> alone.
        /// </summary>
        /// <typeparam name="T">The document type.</typeparam>
        /// <param name="item">The document to create.</param>
        /// <param name="partitionKey">The single-level partition key value.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>The created document.</returns>
        /// <remarks>
        /// Serves the retired transcript shape. Replaced by <see cref="ExecuteBatchAsync"/>, which
        /// creates the header and its seeded message atomically.
        /// </remarks>
        [Obsolete("Single-level partition key. Use ExecuteBatchAsync; removed when the per-message transcript lands.")]
        Task<T> CreateItemAsync<T>(T item, string partitionKey, CancellationToken cancellationToken);

        /// <summary>
        /// Patches a document in a container partitioned on <c>/userId</c> alone.
        /// </summary>
        /// <param name="id">The document identifier.</param>
        /// <param name="partitionKey">The single-level partition key value.</param>
        /// <param name="operations">The patch operations to apply, at most ten.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns><see langword="true"/> when the document was patched; <see langword="false"/> when it does not exist.</returns>
        /// <remarks>
        /// Serves the retired transcript shape. Replaced by the generic overload, which also
        /// returns the patched document.
        /// </remarks>
        [Obsolete("Single-level partition key. Use the generic (userId, conversationId) overload; removed when the per-message transcript lands.")]
        Task<bool> PatchItemAsync(string id, string partitionKey, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Provides implementation for Azure Cosmos DB operations.
    /// </summary>
    /// <remarks>
    /// Stateless over a singleton <see cref="CosmosClient"/>, and registered as a singleton itself.
    /// </remarks>
    /// <param name="cosmosClient">The client used to reach the account.</param>
    /// <param name="options">The validated Cosmos settings naming the database and container.</param>
    public class AzureCosmosService(CosmosClient cosmosClient, IOptions<CosmosOptions> options) : IAzureCosmosService
    {
        /// <summary>
        /// The maximum number of operations Cosmos DB accepts in a single patch request.
        /// </summary>
        private const int MaxPatchOperations = 10;

        /// <summary>
        /// The maximum number of operations Cosmos DB accepts in a single transactional batch.
        /// </summary>
        private const int MaxBatchOperations = 100;

        /// <summary>
        /// How many identifiers a purge reads per round trip before deleting them.
        /// </summary>
        private const int PurgePageSize = MaxBatchOperations;

        private readonly Container _container = cosmosClient
            .GetDatabase(options.Value.DatabaseId)
            .GetContainer(options.Value.TranscriptContainerId);

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs (except for NotFound, which returns <see langword="null"/>).</exception>
        public async Task<T?> GetItemAsync<T>(string id, Guid userId, Guid conversationId, CancellationToken cancellationToken)
        {
            try
            {
                return await _container.ReadItemAsync<T>(id, BuildPartitionKey(userId, conversationId), cancellationToken: cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when more than ten operations are supplied.</exception>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during the patch.</exception>
        public async Task<T?> PatchItemAsync<T>(string id, Guid userId, Guid conversationId, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, MaxPatchOperations);
            ArgumentOutOfRangeException.ThrowIfZero(operations.Count);

            var requestOptions = new PatchItemRequestOptions { EnableContentResponseOnWrite = true };

            try
            {
                var response = await _container.PatchItemAsync<T>(id, BuildPartitionKey(userId, conversationId), [.. operations], requestOptions, cancellationToken);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxItemCount"/> is not positive.</exception>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during query execution.</exception>
        public async Task<CosmosPage<T>> QueryPageAsync<T>(QueryDefinition query, Guid userId, Guid conversationId, int maxItemCount, string? continuationToken, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItemCount);

            var requestOptions = new QueryRequestOptions
            {
                PartitionKey = BuildPartitionKey(userId, conversationId),
                MaxItemCount = maxItemCount
            };

            using var iterator = _container.GetItemQueryIterator<T>(query, continuationToken, requestOptions);
            if (!iterator.HasMoreResults)
            {
                return new CosmosPage<T>([], null);
            }

            var response = await iterator.ReadNextAsync(cancellationToken);
            return new CosmosPage<T>([.. response], response.ContinuationToken);
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the batch is empty, exceeds one hundred operations, or contains a patch of more than ten operations.</exception>
        public async Task ExecuteBatchAsync(Guid userId, Guid conversationId, IReadOnlyList<CosmosBatchOperation> operations, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfZero(operations.Count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, MaxBatchOperations);

            foreach (var operation in operations)
            {
                if (operation is PatchItemOperation patch)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(patch.Operations.Count, MaxPatchOperations);
                }
            }

            var batch = _container.CreateTransactionalBatch(BuildPartitionKey(userId, conversationId));
            foreach (var operation in operations)
            {
                operation.AddTo(batch);
            }

            using var response = await batch.ExecuteAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // Every sibling of the offending operation comes back as FailedDependency, so the first
            // status that is not that one names the operation that actually broke the batch.
            var failedIndex = 0;
            for (var index = 0; index < response.Count; index++)
            {
                if (response[index].StatusCode != HttpStatusCode.FailedDependency)
                {
                    failedIndex = index;
                    break;
                }
            }

            var failedStatus = response.Count > failedIndex ? response[failedIndex].StatusCode : response.StatusCode;
            throw new CosmosBatchFailedException(
                failedIndex,
                failedStatus,
                $"Transactional batch failed at operation {failedIndex} with status {(int)failedStatus} ({failedStatus}). No document in the batch was written.");
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during the purge.</exception>
        /// <remarks>
        /// Reads identifiers a page at a time and deletes each page as a batch, rather than calling
        /// the server-side delete-by-partition-key operation: that operation is in preview, needs
        /// an account-level capability this deployment does not assume, and is unavailable on the
        /// emulator the storage tests run against. The batch form is a page of round trips instead
        /// of one, which a purge can afford.
        /// </remarks>
        public async Task<int> PurgeConversationAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
        {
            var query = new QueryDefinition("SELECT VALUE c.id FROM c");
            var deleted = 0;

            while (true)
            {
                // No continuation token: each page's documents are deleted before the next read, so
                // resuming from a token would skip past documents that have shifted forward.
                var page = await QueryPageAsync<string>(query, userId, conversationId, PurgePageSize, null, cancellationToken);
                if (page.Items.Count == 0)
                {
                    return deleted;
                }

                await ExecuteBatchAsync(userId, conversationId, [.. page.Items.Select(CosmosBatchOperation.DeleteItem)], cancellationToken);
                deleted += page.Items.Count;
            }
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs (except for NotFound, which returns <see langword="null"/>).</exception>
        [Obsolete("Single-level partition key. Use the (userId, conversationId) overload; removed when the per-message transcript lands.")]
        public async Task<T?> GetItemAsync<T>(string id, string partitionKey, CancellationToken cancellationToken)
        {
            try
            {
                return await _container.ReadItemAsync<T>(id, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during item creation.</exception>
        [Obsolete("Single-level partition key. Use ExecuteBatchAsync; removed when the per-message transcript lands.")]
        public async Task<T> CreateItemAsync<T>(T item, string partitionKey, CancellationToken cancellationToken)
        {
            return await _container.CreateItemAsync(item, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during the patch.</exception>
        [Obsolete("Single-level partition key. Use the generic (userId, conversationId) overload; removed when the per-message transcript lands.")]
        public async Task<bool> PatchItemAsync(string id, string partitionKey, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, MaxPatchOperations);

            var options = new PatchItemRequestOptions { EnableContentResponseOnWrite = false };

            try
            {
                await _container.PatchItemAsync<dynamic>(id, new PartitionKey(partitionKey), [.. operations], options, cancellationToken);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        /// <summary>
        /// Builds the complete two-level partition key addressing one conversation.
        /// </summary>
        private static PartitionKey BuildPartitionKey(Guid userId, Guid conversationId) =>
            new PartitionKeyBuilder()
                .Add(userId.ToString())
                .Add(conversationId.ToString())
                .Build();
    }
}
