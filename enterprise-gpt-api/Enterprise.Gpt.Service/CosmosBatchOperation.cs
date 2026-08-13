using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Service;

/// <summary>
/// One operation within a transactional batch, expressed as data rather than as a call against
/// the SDK's fluent builder.
/// </summary>
/// <remarks>
/// <see cref="TransactionalBatch"/> is an abstract fluent builder, which a test double cannot
/// meaningfully observe. Describing a batch as a list of records instead lets the service map it
/// onto the real builder while callers assert on the shape they submitted. The hierarchy is closed
/// — the abstract member is internal — so the service can exhaustively translate it.
/// </remarks>
public abstract record CosmosBatchOperation
{
    /// <summary>
    /// Appends this operation to the SDK batch under construction.
    /// </summary>
    internal abstract void AddTo(TransactionalBatch batch);

    /// <summary>
    /// Creates an item, failing the batch if one with the same identifier already exists.
    /// </summary>
    /// <typeparam name="T">The document type, preserved so serialization uses it rather than <see cref="object"/>.</typeparam>
    /// <param name="item">The document to create.</param>
    /// <returns>The operation.</returns>
    public static CosmosBatchOperation CreateItem<T>(T item) where T : notnull => new CreateItemOperation<T>(item);

    /// <summary>
    /// Applies partial-update operations to an existing item.
    /// </summary>
    /// <param name="id">The identifier of the item to patch.</param>
    /// <param name="operations">The patch operations to apply.</param>
    /// <returns>The operation.</returns>
    public static CosmosBatchOperation PatchItem(string id, IReadOnlyList<PatchOperation> operations) =>
        new PatchItemOperation(id, operations);

    /// <summary>
    /// Deletes an item.
    /// </summary>
    /// <param name="id">The identifier of the item to delete.</param>
    /// <returns>The operation.</returns>
    public static CosmosBatchOperation DeleteItem(string id) => new DeleteItemOperation(id);
}

/// <summary>
/// Creates <paramref name="Item"/> within the batch.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Item">The document to create.</param>
public sealed record CreateItemOperation<T>(T Item) : CosmosBatchOperation where T : notnull
{
    /// <inheritdoc />
    internal override void AddTo(TransactionalBatch batch) =>
        batch.CreateItem(Item, new TransactionalBatchItemRequestOptions { EnableContentResponseOnWrite = false });
}

/// <summary>
/// Applies <paramref name="Operations"/> to the item identified by <paramref name="Id"/>.
/// </summary>
/// <param name="Id">The identifier of the item to patch.</param>
/// <param name="Operations">The patch operations to apply.</param>
public sealed record PatchItemOperation(string Id, IReadOnlyList<PatchOperation> Operations) : CosmosBatchOperation
{
    /// <inheritdoc />
    internal override void AddTo(TransactionalBatch batch) =>
        batch.PatchItem(Id, [.. Operations], new TransactionalBatchPatchItemRequestOptions { EnableContentResponseOnWrite = false });
}

/// <summary>
/// Deletes the item identified by <paramref name="Id"/>.
/// </summary>
/// <param name="Id">The identifier of the item to delete.</param>
public sealed record DeleteItemOperation(string Id) : CosmosBatchOperation
{
    /// <inheritdoc />
    internal override void AddTo(TransactionalBatch batch) => batch.DeleteItem(Id);
}
