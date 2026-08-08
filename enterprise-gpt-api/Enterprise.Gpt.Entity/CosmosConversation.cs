using Enterprise.Gpt.Common.Enums;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Entity
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

        // Deliberately the Cosmos projection, not the SQL ConversationDocument entity: that entity carries
        // a Chunks collection whose SqlVector<float> embeddings would be serialized into the transcript.
        [JsonPropertyName("documents")]
        public List<CosmosConversationDocument> Documents { get; set; } = [];

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

    /// <summary>
    /// What a transcript message's turn consumed in total — the assistant's own model turns plus
    /// every tool, MCP call and agent underneath them.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same split as the identically named columns on
    /// <see cref="ConversationUsage"/>, which hold the assistant's share alone and keep the tool
    /// share in <see cref="ConversationUsage.ToolInputTokens"/>. The transcript is what a reader
    /// sees beside the message, and the number that belongs there is what the turn cost; the
    /// breakdown lives in the relational audit trail.
    /// </remarks>
    public class CosmosConversationUsage
    {
        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }
    }
}
