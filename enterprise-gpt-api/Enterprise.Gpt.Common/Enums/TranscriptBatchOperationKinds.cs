using System.Net;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Common.Enums;

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
