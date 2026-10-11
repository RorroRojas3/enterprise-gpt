using Enterprise.Gpt.Common.Enums;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Repository.Cosmos.Models;

/// <summary>
/// One recorded operation in a <see cref="TranscriptBatch"/>.
/// </summary>
/// <remarks>
/// <c>Apply</c> keeps the document's static type for <c>CreateItem&lt;T&gt;</c>, since System.Text.Json
/// serializes a boxed <see cref="object"/> as empty; the other members let a test double read the batch.
/// </remarks>
public sealed record TranscriptBatchOperation(
    TranscriptBatchOperationKinds Kind,
    string Id,
    object? Item,
    IReadOnlyList<PatchOperation>? PatchOperations,
    Action<TransactionalBatch> Apply);
