using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto
{
    public class ConversationDocumentDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("conversationId")]
        public Guid ConversationId { get; set; }

        [JsonPropertyName("documentId")]
        public Guid DocumentId => Id;

        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;
    }
}
