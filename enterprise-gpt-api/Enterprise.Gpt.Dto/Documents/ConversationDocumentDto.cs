using System.Text.Json.Serialization;
using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// A document held by a conversation, as returned by the conversation document listing and
    /// the upload pipeline. Carries no chunk or embedding data.
    /// </summary>
    public class ConversationDocumentDto
    {
        /// <summary>Gets or sets the unique identifier of the document.</summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>Gets or sets the owning conversation.</summary>
        [JsonPropertyName("conversationId")]
        public Guid ConversationId { get; set; }

        /// <summary>
        /// Alias of <see cref="Id"/>, matching the shape the upload-status response uses so a client
        /// can key both listings the same way.
        /// </summary>
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
        /// Serialized as the member name, for the reason <see cref="ConversationMessageDto.TokenAccuracy"/>
        /// records. Defaulted rather than left unset, because the converter writes an undefined value as a
        /// number and the enum has no zero member.
        /// </remarks>
        [JsonPropertyName("type")]
        [JsonConverter(typeof(JsonStringEnumConverter<ConversationDocumentTypes>))]
        public ConversationDocumentTypes Type { get; set; } = ConversationDocumentTypes.Uploaded;

        /// <summary>Gets or sets when the document was stored.</summary>
        [JsonPropertyName("dateCreated")]
        public DateTimeOffset DateCreated { get; set; }
    }
}
