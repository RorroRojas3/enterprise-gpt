using Enterprise.Gpt.Common.Enums;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Repository.Cosmos.Models;

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
