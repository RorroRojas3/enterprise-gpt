using System.Collections.Concurrent;
using System.Net;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository.Cosmos;
using Enterprise.Gpt.Repository.Cosmos.Models;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// In-memory <see cref="ITranscriptStore"/> so endpoints that write a conversation transcript can
/// be exercised without a Cosmos DB account or the emulator.
/// </summary>
/// <remarks>
/// Patches are ignored and <see cref="QueryAsync"/> throws rather than return an empty page a read test
/// could pass on: JSON-patch semantics and ordered queries are proved against the emulator instead.
/// </remarks>
public sealed class FakeTranscriptStore : ITranscriptStore
{
    private readonly ConcurrentDictionary<string, object> _items = new();

    public void Reset()
    {
        _items.Clear();
    }

    /// <inheritdoc />
    public Task<T?> ReadItemAsync<T>(string id, PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        return Task.FromResult(_items.TryGetValue(Key(id, partitionKey), out var item) ? (T?)item : default);
    }

    /// <inheritdoc />
    public Task<TranscriptPage<T>> QueryAsync<T>(
        QueryDefinition query,
        PartitionKey partitionKey,
        int? pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Transcript queries are deliberately unimplemented here. Ordering and filtering across documents is what the Cosmos emulator tests exist to prove; assert on them there.");
    }

    /// <inheritdoc />
    public Task<bool> PatchItemAsync(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> operations,
        CancellationToken cancellationToken)
    {
        // Mirrors the real store's guard so an over-long patch fails here, not only in production.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, CosmosTranscriptStore.MaxPatchOperations);

        return Task.FromResult(_items.ContainsKey(Key(id, partitionKey)));
    }

    /// <inheritdoc />
    public Task<TranscriptBatchResult> ExecuteBatchAsync(
        PartitionKey partitionKey,
        Action<TranscriptBatch> build,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);

        var batch = new TranscriptBatch();
        build(batch);

        ArgumentOutOfRangeException.ThrowIfZero(batch.Operations.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch.Operations.Count, TranscriptBatch.MaxOperations);

        // Applied only after every operation is checked, so a failing batch leaves nothing behind.
        for (var index = 0; index < batch.Operations.Count; index++)
        {
            var operation = batch.Operations[index];

            if (operation.Kind is TranscriptBatchOperationKinds.Create
                && _items.ContainsKey(Key(operation.Id, partitionKey)))
            {
                return Task.FromResult(new TranscriptBatchResult(
                    false, HttpStatusCode.Conflict, index, HttpStatusCode.Conflict));
            }
        }

        foreach (var operation in batch.Operations)
        {
            if (operation.Kind == TranscriptBatchOperationKinds.Create)
            {
                _items[Key(operation.Id, partitionKey)] = operation.Item!;
            }
        }

        return Task.FromResult(new TranscriptBatchResult(true, HttpStatusCode.OK, null, null));
    }

    private static string Key(string id, PartitionKey partitionKey) => $"{partitionKey}/{id}";
}
