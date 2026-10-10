using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// Which table a retrievable document and its chunks live in.
/// </summary>
/// <remarks>
/// Persisted nowhere: it exists only to keep the two structurally identical chunk tables apart once
/// their rows have been unioned into a single candidate set, so a conversation document and a project
/// document that happen to share a chunk index are never confused for one another.
/// </remarks>
public enum DocumentSources : byte
{
    /// <summary>A document uploaded into the conversation itself.</summary>
    Conversation = 0,

    /// <summary>A document uploaded into the conversation's project.</summary>
    Project = 1
}
