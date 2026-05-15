using Microsoft.Azure.Cosmos;
using System.Net;

/// <summary>
/// Contains services for Azure Cosmos DB operations.
/// </summary>
namespace RR.AI_Chat.Service
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
        Task<T> UpdateItemAsync<T>(T item, string id, string partitionKey, CancellationToken cancellationToken);

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
        /// <exception cref="CosmosException">Thrown when a Cosmos DB error occurs during item deletion.</exception>
        public async Task DeleteItemAsync(string id, string partitionKey, CancellationToken cancellationToken)
        {
            await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
        }
    }
}
