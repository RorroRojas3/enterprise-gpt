using Enterprise.Gpt.Common.Enums;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Repository.Cosmos.Models;

/// <summary>
/// Accumulates the operations that will run as one transaction against a single partition.
/// </summary>
/// <remarks>
/// A recorded operation list rather than the Cosmos <see cref="TransactionalBatch"/> itself:
/// that type is abstract and obtainable only from a live <c>Container</c>, so exposing it on
/// <see cref="ITranscriptStore"/> would make the interface impossible to substitute.
/// </remarks>
/// <example>
/// <code language="csharp">
/// var result = await store.ExecuteBatchAsync(new PartitionKey(pk), batch => batch
///     .Create(userMessage.Id.ToString(), userMessage)
///     .Create(assistantMessage.Id.ToString(), assistantMessage)
///     .Patch(header.Id.ToString(),
///     [
///         PatchOperation.Increment("/messageCount", 2),
///         PatchOperation.Increment("/contextTokens", contextTokens),
///         PatchOperation.Set("/dateModified", completedAt),
///     ]), cancellationToken);
/// </code>
/// </example>
public sealed class TranscriptBatch
{
    /// <summary>
    /// The largest number of operations Cosmos DB accepts in one transactional batch.
    /// </summary>
    public const int MaxOperations = 100;

    private readonly List<TranscriptBatchOperation> _operations = [];

    /// <summary>
    /// Gets the operations recorded so far, in the order they will be applied.
    /// </summary>
    public IReadOnlyList<TranscriptBatchOperation> Operations => _operations;

    /// <summary>
    /// Adds a document creation to the batch.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="id">The document's identifier, used only to describe the operation.</param>
    /// <param name="item">The document to create.</param>
    /// <returns>The same batch, so operations can be chained.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is <see langword="null"/>.</exception>
    public TranscriptBatch Create<T>(string id, T item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(item);

        _operations.Add(new TranscriptBatchOperation(
            TranscriptBatchOperationKinds.Create, id, item, null, batch => batch.CreateItem(item)));

        return this;
    }

    /// <summary>
    /// Adds a partial update to the batch.
    /// </summary>
    /// <param name="id">The identifier of the document to patch.</param>
    /// <param name="operations">The updates to apply, in order.</param>
    /// <returns>The same batch, so operations can be chained.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when more than <see cref="CosmosTranscriptStore.MaxPatchOperations"/> updates are supplied,
    /// which Cosmos DB rejects.
    /// </exception>
    public TranscriptBatch Patch(string id, IReadOnlyList<PatchOperation> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, CosmosTranscriptStore.MaxPatchOperations);

        _operations.Add(new TranscriptBatchOperation(
            TranscriptBatchOperationKinds.Patch, id, null, operations, batch => batch.PatchItem(id, operations)));

        return this;
    }

    /// <summary>
    /// Adds a document deletion to the batch.
    /// </summary>
    /// <param name="id">The identifier of the document to delete.</param>
    /// <returns>The same batch, so operations can be chained.</returns>
    public TranscriptBatch Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        _operations.Add(new TranscriptBatchOperation(
            TranscriptBatchOperationKinds.Delete, id, null, null, batch => batch.DeleteItem(id)));

        return this;
    }
}
