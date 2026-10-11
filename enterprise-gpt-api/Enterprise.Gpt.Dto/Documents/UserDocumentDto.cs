using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Documents;

/// <summary>
/// A conversation document as returned by the caller-scoped cross-conversation listing, carrying
/// the owning conversation's name so the documents library needs no per-conversation fan-out.
/// </summary>
public class UserDocumentDto : ConversationDocumentDto
{
    [JsonPropertyName("conversationName")]
    public string ConversationName { get; set; } = null!;

    /// <summary>How many of the conversation's active documents match this listing's filters.</summary>
    /// <remarks>
    /// Computed per response, so a grouped client can label a partially loaded conversation correctly.
    /// </remarks>
    [JsonPropertyName("conversationDocumentCount")]
    public int ConversationDocumentCount { get; set; }
}
