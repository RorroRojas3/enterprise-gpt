using Microsoft.Azure.Cosmos;
using System.Net;

/// <summary>
/// Contains services for Azure Cosmos DB operations.
/// </summary>
namespace Enterprise.Gpt.Service
{
    /// <summary>
    /// Defines operations for interacting with Azure Cosmos DB.
    /// </summary>
    public interface IAzureCosmosService
    {
        /// <summary>
        /// Retrieves a single item from the Cosmos DB container.
        /// </summary>
        /// <typeparam name="T">The type of the item to retrieve.</typeparam>
        /// <param name="id">The unique identifier of the item.</param>
        /// <param name="partitionKey">The partition key value of the item.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the item if found; otherwise, <see langword="null"/>.</returns>
        Task<T?> GetItemAsync<T>(string id, string partitionKey, CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves multiple items from the Cosmos DB container based on a query.
        /// </summary>
        /// <typeparam name="T">The type of the items to retrieve.</typeparam>
        /// <param name="query">The SQL query string to execute.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a collection of items matching the query.</returns>
        Task<IEnumerable<T>> GetItemsAsync<T>(string query);

        /// <summary>
        /// Creates a new item in the Cosmos DB container.
        /// </summary>
        /// <typeparam name="T">The type of the item to create.</typeparam>
        /// <param name="item">The item to create.</param>
        /// <param name="partitionKey">The partition key value for the item.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the created item.</returns>
        Task<T> CreateItemAsync<T>(T item, string partitionKey, CancellationToken cancellationToken);

        /// <summary>
        /// Updates an existing item in the Cosmos DB container.
        /// </summary>
        /// <typeparam name="T">The type of the item to update.</typeparam>
        /// <param name="item">The updated item.</param>
        /// <param name="id">The unique identifier of the item to update.</param>
        /// <param name="partitionKey">The partition key value of the item.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the updated item.</returns>
        /// <remarks>
        /// This replaces the whole document unconditionally, so anything written between the read
        /// that produced <paramref name="item"/> and this call is silently lost. Prefer
        /// <see cref="PatchItemAsync"/> whenever only some fields change — it needs no prior read
        /// and therefore has no lost-update window.
        /// </remarks>
        Task<T> UpdateItemAsync<T>(T item, string id, string partitionKey, CancellationToken cancellationToken);

        /// <summary>
        /// Applies a set of partial-update operations to an item without reading and rewriting it.
        /// </summary>
        /// <param name="id">The unique identifier of the item to patch.</param>
        /// <param name="partitionKey">The partition key value of the item.</param>
        /// <param name="operations">The patch operations to apply. Cosmos DB permits at most 10 per call.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result is <see langword="true"/>
        /// if the item was patched, or <see langword="false"/> if no item with that identifier exists.
        /// </returns>
        /// <remarks>
        /// Patching is the only way to mutate a document without a read-modify-write window.
        /// Use it for appends and counters so concurrent writers cannot lose each other's updates.
        /// </remarks>
        /// <example>
        /// <code language="csharp">
        /// await cosmosService.PatchItemAsync(id, userId,
        /// [
        ///     PatchOperation.Add("/messages/-", message),
        ///     PatchOperation.Increment("/totalTokens", tokens),
        /// ], cancellationToken);
        /// </code>
        /// </example>
        Task<bool> PatchItemAsync(string id, string partitionKey, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes an item from the Cosmos DB container.
        /// </summary>
        /// <param name="id">The unique identifier of the item to delete.</param>
        /// <param name="partitionKey">The partition key value of the item.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous delete operation.</returns>
        Task DeleteItemAsync(string id, string partitionKey, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Provides implementation for Azure Cosmos DB operations.
    /// </summary>
    /// <remarks>
    /// This service wraps the <see cref="CosmosClient"/> to provide simplified CRUD operations
    /// against a specific Cosmos DB container.
    /// </remarks>
    public class AzureCosmosService : IAzureCosmosService
    {
        /// <summary>
        /// The maximum number of operations Cosmos DB accepts in a single patch request.
        /// </summary>
        private const int MaxPatchOperations = 10;

        private readonly CosmosClient _cosmosClient;
        private readonly Container _container;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureCosmosService"/> class.
        /// </summary>
        /// <param name="cosmosClient">The Cosmos DB client used to connect to the database.</param>
        /// <param name="databaseId">The identifier of the database to connect to.</param>
        /// <param name="containerId">The identifier of the container within the database.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="cosmosClient"/> is <see langword="null"/>.</exception>
        public AzureCosmosService(CosmosClient cosmosClient, string databaseId, string containerId)
        {
            _cosmosClient = cosmosClient;
            var database = _cosmosClient.GetDatabase(databaseId);
            _container = database.GetContainer(containerId);
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs (except for NotFound, which returns <see langword="null"/>).</exception>
        public async Task<T?> GetItemAsync<T>(string id, string partitionKey, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _container.ReadItemAsync<T>(id, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
                return response;
            }
            catch (CosmosException ex)
            {
                if (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    return default;
                }
                else
                {
                    throw;
                }
            }
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during query execution.</exception>
        public async Task<IEnumerable<T>> GetItemsAsync<T>(string query)
        {
            var items = new List<T>();
            var iterator = _container.GetItemQueryIterator<T>(query);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                items.AddRange(response);
            }

            return items;
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during item creation.</exception>
        public async Task<T> CreateItemAsync<T>(T item, string partitionKey, CancellationToken cancellationToken)
        {
            var response = await _container.CreateItemAsync(item, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
            return response;
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during item update.</exception>
        public async Task<T> UpdateItemAsync<T>(T item, string id, string partitionKey, CancellationToken cancellationToken)
        {
            var response = await _container.ReplaceItemAsync(item, id, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
            return response;
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during the patch.</exception>
        public async Task<bool> PatchItemAsync(string id, string partitionKey, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, MaxPatchOperations);

            // Suppressing the content response matters more here than on other writes: the patched
            // resource is a whole conversation transcript, and returning it on every appended
            // message would undo most of the bandwidth saved by patching instead of replacing.
            var options = new PatchItemRequestOptions { EnableContentResponseOnWrite = false };

            try
            {
                await _container.PatchItemAsync<dynamic>(id, new PartitionKey(partitionKey), operations, options, cancellationToken);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        /// <inheritdoc/>
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during item deletion.</exception>
        public async Task DeleteItemAsync(string id, string partitionKey, CancellationToken cancellationToken)
        {
            await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
        }
    }
}
