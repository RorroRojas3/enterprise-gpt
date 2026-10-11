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
/// <para>
/// Documents are stored whole, keyed by partition key and id. A batch's creates are applied — which
/// is what makes a header readable after the turn that wrote it — but patch operations are ignored,
/// because the JSON-patch semantics Cosmos applies are exactly what a fake gets subtly wrong, and no
/// test here reads a patched value back.
/// </para>
/// <para>
/// <see cref="QueryAsync"/> throws on purpose. Ordered, filtered, multi-document queries are the
/// thing this fake cannot prove, and returning an empty page would let a read-path test pass
/// without reading anything. Those assertions belong to the emulator-backed tests instead.
/// </para>
/// </remarks>
public sealed class FakeTranscriptStore : ITranscriptStore
{
    private readonly ConcurrentDictionary<string, object> _items = new();

    /// <summary>
    /// Clears every stored document.
    /// </summary>
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
        // Mirrors the real store's guard so an over-long patch fails here rather than only in
        // production, where Cosmos rejects the request outright.
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

        // Applied only once every operation has been inspected, so a batch that would fail leaves
        // nothing behind — the atomicity the production path depends on.
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
