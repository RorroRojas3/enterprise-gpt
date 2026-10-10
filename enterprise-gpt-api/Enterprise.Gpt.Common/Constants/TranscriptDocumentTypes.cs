using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// The <c>type</c> discriminator values that share the transcript container.
/// </summary>
/// <remarks>
/// The container is heterogeneous by design, and this discriminator stays in every read predicate.
/// That is what stops a third document kind, added later, from silently entering queries written
/// before it existed.
/// </remarks>
public static class TranscriptDocumentTypes
{
    /// <summary>A conversation's header document, one per conversation.</summary>
    public const string Conversation = "conversation";

    /// <summary>A single transcript message, one per message.</summary>
    public const string Message = "message";
}
