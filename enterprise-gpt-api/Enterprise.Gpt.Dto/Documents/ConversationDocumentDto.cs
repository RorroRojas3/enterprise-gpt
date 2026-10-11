using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Conversations;

namespace Enterprise.Gpt.Dto.Documents;

/// <summary>A conversation document as listed or uploaded; carries no chunk or embedding data.</summary>
public class ConversationDocumentDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; set; }

    /// <summary>Alias of <see cref="Id"/> so clients key this and upload-status responses alike.</summary>
    [JsonPropertyName("documentId")]
    public Guid DocumentId => Id;

    /// <summary>Gets or sets the original file name as uploaded.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    /// <summary>Gets or sets the lower-cased file extension, including the leading dot.</summary>
    [JsonPropertyName("extension")]
    public string Extension { get; set; } = null!;

    /// <summary>Gets or sets the content type reported by the client at upload time.</summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = null!;

    /// <summary>Gets or sets the file size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Gets or sets whether a user uploaded this document or the assistant produced it.</summary>
    /// <remarks>
    /// Serialized by name, as <see cref="ConversationMessageDto.TokenAccuracy"/> is. Defaulted because the
    /// enum has no zero member and the converter writes an undefined value as a number.
    /// </remarks>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<ConversationDocumentTypes>))]
    public ConversationDocumentTypes Type { get; set; } = ConversationDocumentTypes.Uploaded;

    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; set; }
}
