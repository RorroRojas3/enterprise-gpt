namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// What a single operation in a <c>TranscriptBatch</c> does.
/// </summary>
public enum TranscriptBatchOperationKinds
{
    /// <summary>Creates a document, failing the batch if the identifier already exists.</summary>
    Create = 1,

    /// <summary>Applies partial-update operations to an existing document.</summary>
    Patch = 2
}
