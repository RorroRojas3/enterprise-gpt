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
}
