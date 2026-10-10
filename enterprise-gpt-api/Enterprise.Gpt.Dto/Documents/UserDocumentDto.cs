using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto;

/// <summary>
/// A conversation document as returned by the caller-scoped cross-conversation listing, carrying
/// the owning conversation's name so the documents library needs no per-conversation fan-out.
/// </summary>
public class UserDocumentDto : ConversationDocumentDto
{
    /// <summary>Gets or sets the name of the conversation the document belongs to.</summary>
    [JsonPropertyName("conversationName")]
    public string ConversationName { get; set; } = null!;

    /// <summary>
    /// Gets or sets how many of the owning conversation's active documents match the same filters
    /// this listing was narrowed by.
    /// </summary>
    /// <remarks>
    /// Computed per response, never persisted. It is what lets a grouped client label a partially
    /// loaded conversation honestly instead of counting only the rows it happens to hold.
    /// </remarks>
    [JsonPropertyName("conversationDocumentCount")]
    public int ConversationDocumentCount { get; set; }
}
