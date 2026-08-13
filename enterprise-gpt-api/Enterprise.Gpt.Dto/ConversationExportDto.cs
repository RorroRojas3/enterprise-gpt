using Enterprise.Gpt.Common.Enums;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// A conversation as it leaves the platform.
    /// </summary>
    /// <remarks>
    /// The property names mirror the stored transcript document rather than the HTTP DTOs, so an
    /// export is the storage shape rather than a third representation to keep in step.
    /// </remarks>
    public class ConversationExportDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("dateCreated")]
        public DateTimeOffset DateCreated { get; set; }

        [JsonPropertyName("dateModified")]
        public DateTimeOffset DateModified { get; set; }

        [JsonPropertyName("messages")]
        public List<ConversationExportMessageDto> Messages { get; set; } = [];
    }

    /// <summary>
    /// One exported message.
    /// </summary>
    public class ConversationExportMessageDto
    {
        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = null!;

        [JsonPropertyName("content")]
        public string Content { get; set; } = null!;

        [JsonPropertyName("htmlContent")]
        public string? HtmlContent { get; set; }

        [JsonPropertyName("tokens")]
        public long Tokens { get; set; }

        [JsonPropertyName("tokenAccuracy")]
        public TokenAccuracies TokenAccuracy { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("dateCreated")]
        public DateTimeOffset DateCreated { get; set; }

        /// <summary>
        /// Gets or sets what the turn that produced this message was billed, on assistant messages
        /// only.
        /// </summary>
        /// <remarks>
        /// Carries the turn's totals and no per-tool breakdown: that detail lives in the relational
        /// audit trail and is exposed over HTTP nowhere.
        /// </remarks>
        [JsonPropertyName("usage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ConversationExportUsageDto? Usage { get; set; }
    }

    /// <summary>
    /// What an exported turn was billed.
    /// </summary>
    public class ConversationExportUsageDto
    {
        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }
    }
}
