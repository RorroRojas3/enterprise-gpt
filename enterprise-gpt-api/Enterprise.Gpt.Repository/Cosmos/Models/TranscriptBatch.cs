using Enterprise.Gpt.Common.Enums;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Repository.Cosmos.Models;

/// <summary>
/// Accumulates the operations that will run as one transaction against a single partition.
/// </summary>
/// <remarks>
/// A recorded list rather than <see cref="TransactionalBatch"/>, which only a live <c>Container</c> can
/// create, so <see cref="ITranscriptStore"/> stays substitutable.
/// </remarks>
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
    /// Adds a document creation to the batch; <paramref name="id"/> only describes the operation.
    /// </summary>
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
}
