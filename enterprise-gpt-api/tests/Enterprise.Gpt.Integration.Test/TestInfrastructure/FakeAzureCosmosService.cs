using System.Collections.Concurrent;
using Microsoft.Azure.Cosmos;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// In-memory <see cref="IAzureCosmosService"/> so endpoints that write the conversation transcript
/// can be exercised without a Cosmos DB account or the emulator.
/// </summary>
/// <remarks>
/// <para>
/// Documents are stored whole, keyed by partition key and id. <see cref="PatchItemAsync"/>
/// deliberately does <em>not</em> interpret the operations: the JSON-patch semantics Cosmos applies
/// are exactly the thing a fake would get subtly wrong, and no test needs the patched value read
/// back. It reports whether the document existed — the return value the production code branches on
/// — and records the operations so a test can assert on what was sent.
/// </para>
/// <para>
/// Consequently this supports tests about the relational side of a conversation write. Assertions
/// about transcript <em>content</em> belong in the unit tests, which substitute this interface.
/// </para>
/// <para>
/// One consequence worth knowing before writing a streaming test: because patches are not applied,
/// a stored <c>CosmosConversation.Messages</c> never grows. Conversation naming fires when that
/// count is one, so every turn against this fake looks like the conversation's first — a test here
/// can neither confirm the name-once rule nor exercise the seed-versus-append branch.
/// </para>
/// </remarks>
public sealed class FakeAzureCosmosService : IAzureCosmosService
{
    /// <summary>
    /// The maximum number of operations Cosmos DB accepts in a single patch request, mirroring the
    /// constant the real service enforces.
    /// </summary>
    private const int MaxPatchOperations = 10;

    private readonly ConcurrentDictionary<string, object> _items = new();
    private readonly ConcurrentQueue<(string Id, IReadOnlyList<PatchOperation> Operations)> _patches = new();

    /// <summary>
    /// Gets the patch calls made since the last reset, in order.
    /// </summary>
    public IReadOnlyCollection<(string Id, IReadOnlyList<PatchOperation> Operations)> Patches => [.. _patches];

    /// <summary>
    /// Clears every stored document and the recorded patch calls.
    /// </summary>
    public void Reset()
    {
        _items.Clear();
        _patches.Clear();
    }

    /// <inheritdoc />
    public Task<T?> GetItemAsync<T>(string id, string partitionKey, CancellationToken cancellationToken)
    {
        return Task.FromResult(_items.TryGetValue(Key(id, partitionKey), out var item) ? (T?)item : default);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not implemented on purpose. Returning every stored item and ignoring the query would let the
    /// first test that uses this pass for the wrong reason; failing loudly is the honest option
    /// until something actually needs it.
    /// </remarks>
    public Task<IEnumerable<T>> GetItemsAsync<T>(string query)
    {
        throw new NotSupportedException(
            "Query support is deliberately unimplemented. Add it here, honouring the query, when a test needs it.");
    }

    /// <inheritdoc />
    public Task<T> CreateItemAsync<T>(T item, string partitionKey, CancellationToken cancellationToken)
    {
        _items[Key(IdOf(item), partitionKey)] = item!;
        return Task.FromResult(item);
    }

    /// <inheritdoc />
    public Task<T> UpdateItemAsync<T>(T item, string id, string partitionKey, CancellationToken cancellationToken)
    {
        _items[Key(id, partitionKey)] = item!;
        return Task.FromResult(item);
    }

    /// <inheritdoc />
    public Task<bool> PatchItemAsync(string id, string partitionKey, IReadOnlyList<PatchOperation> operations, CancellationToken cancellationToken)
    {
        // Mirrors the real service's guard so an over-long patch fails here rather than only in
        // production, where Cosmos rejects the request outright.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, MaxPatchOperations);

        _patches.Enqueue((id, operations));
        return Task.FromResult(_items.ContainsKey(Key(id, partitionKey)));
    }

    /// <inheritdoc />
    public Task DeleteItemAsync(string id, string partitionKey, CancellationToken cancellationToken)
    {
        _items.TryRemove(Key(id, partitionKey), out _);
        return Task.CompletedTask;
    }

    private static string Key(string id, string partitionKey) => $"{partitionKey}/{id}";

    /// <summary>
    /// Reads the document's identifier off its <c>Id</c> property, which every type stored in the
    /// container carries.
    /// </summary>
    private static string IdOf<T>(T item)
    {
        var id = typeof(T).GetProperty("Id")?.GetValue(item);

        return id?.ToString()
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no readable Id property.");
    }
}
