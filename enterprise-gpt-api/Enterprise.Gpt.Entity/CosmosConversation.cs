using RR.AI_Chat.Common.Enums;
using System.Text.Json.Serialization;

namespace RR.AI_Chat.Entity
{
    public class CosmosConversation
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("userId")]
        public Guid UserId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("totalTokens")]
        public long TotalTokens { get; set; }

        [JsonPropertyName("messageCount")]
        public long MessageCount => Messages.Count;

        [JsonPropertyName("documents")]
        public List<ConversationDocument> Documents { get; set; } = [];

        [JsonPropertyName("messages")]
        public List<CosmosConversationMessage> Messages { get; set; } = [];

        [JsonPropertyName("dateCreated")]
        public DateTimeOffset DateCreated { get; set; }

        [JsonPropertyName("dateModified")]
        public DateTimeOffset DateModified { get; set; }

        [JsonPropertyName("dateDeactivated")]
        public DateTimeOffset? DateDeactivated { get; set; }
    }

    public class CosmosConversationDocument
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; } 

        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("extension")]
        public string Extension { get; set; } = null!;

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = null!;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class CosmosConversationMessage
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("role")]
        public ChatRoles Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = null!;

        [JsonPropertyName("dateCreated")]
        public DateTimeOffset DateCreated { get; set; }

        [JsonPropertyName("tokens")]
        public long Tokens { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("usage")]
        public CosmosConversationUsage? Usage { get; set; }
    }

    public class CosmosConversationUsage 
    {
        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }
    }
}
