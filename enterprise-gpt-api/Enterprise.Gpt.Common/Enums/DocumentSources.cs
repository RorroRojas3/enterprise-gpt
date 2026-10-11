namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// Which table a retrievable document and its chunks live in.
/// </summary>
/// <remarks>
/// Never persisted: it keeps the two identical chunk tables apart once their rows are unioned into one
/// candidate set, so equal chunk indexes from different documents are not confused.
/// </remarks>
public enum DocumentSources : byte
{
    /// <summary>A document uploaded into the conversation itself.</summary>
    Conversation = 0,

    /// <summary>A document uploaded into the conversation's project.</summary>
    Project = 1
}
