using System.Net;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Service.Transcripts;

/// <summary>
/// What a single operation in a <see cref="TranscriptBatch"/> does.
/// </summary>
public enum TranscriptBatchOperationKinds
{
    /// <summary>Creates a document, failing the batch if the identifier already exists.</summary>
    Create = 1,

    /// <summary>Applies partial-update operations to an existing document.</summary>
    Patch = 2,

    /// <summary>Deletes a document.</summary>
    Delete = 3
}

/// <summary>
/// One recorded operation in a <see cref="TranscriptBatch"/>.
/// </summary>
/// <param name="Kind">What the operation does.</param>
/// <param name="Id">The identifier of the document it acts on.</param>
/// <param name="Item">The document, for a <see cref="TranscriptBatchOperationKinds.Create"/>; otherwise <see langword="null"/>.</param>
/// <param name="PatchOperations">The partial updates, for a <see cref="TranscriptBatchOperationKinds.Patch"/>; otherwise <see langword="null"/>.</param>
/// <param name="Apply">Adds this operation to the Cosmos DB batch that will execute it.</param>
/// <remarks>
/// Carries both a description of the operation and the delegate that performs it. The delegate is
/// what preserves the document's static type through <c>TransactionalBatch.CreateItem&lt;T&gt;</c> —
/// boxing the item to <see cref="object"/> and creating it as one would serialize an empty
/// document, because System.Text.Json writes the declared type's properties, not the runtime
/// type's. The description is what lets a test double interpret a batch it cannot execute.
/// </remarks>
public sealed record TranscriptBatchOperation(
    TranscriptBatchOperationKinds Kind,
    string Id,
    object? Item,
    IReadOnlyList<PatchOperation>? PatchOperations,
    Action<TransactionalBatch> Apply);

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
    /// Thrown when more than <see cref="TranscriptStore.MaxPatchOperations"/> updates are supplied,
    /// which Cosmos DB rejects.
    /// </exception>
    public TranscriptBatch Patch(string id, IReadOnlyList<PatchOperation> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operations.Count, TranscriptStore.MaxPatchOperations);

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

/// <summary>
/// The outcome of a transactional batch.
/// </summary>
/// <param name="IsSuccess">Whether every operation was applied.</param>
/// <param name="StatusCode">The status the batch as a whole returned.</param>
/// <param name="FailedOperationIndex">
/// The zero-based index of the operation that caused the failure, or <see langword="null"/> when
/// the batch succeeded or no single operation can be blamed.
/// </param>
/// <param name="FailedOperationStatusCode">
/// The status the failing operation returned, or <see langword="null"/> when the batch succeeded.
/// </param>
/// <remarks>
/// The failing operation is identifiable because Cosmos DB returns that operation's own status and
/// marks every other operation <see cref="HttpStatusCode.FailedDependency"/> (424) — so the one
/// entry whose status is neither success nor 424 is the cause.
/// </remarks>
public sealed record TranscriptBatchResult(
    bool IsSuccess,
    HttpStatusCode StatusCode,
    int? FailedOperationIndex,
    HttpStatusCode? FailedOperationStatusCode)
{
    /// <summary>
    /// Describes the failure for a log message, without including any document content.
    /// </summary>
    /// <returns>A short description of which operation failed and how.</returns>
    public string Describe()
    {
        return IsSuccess
            ? $"batch succeeded ({(int)StatusCode})"
            : FailedOperationIndex is { } index
                ? $"batch failed at operation {index} with {(int)(FailedOperationStatusCode ?? StatusCode)}"
                : $"batch failed with {(int)StatusCode}";
    }
}
