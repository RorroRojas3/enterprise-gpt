using System.Text.Json.Serialization;
using Enterprise.Gpt.Entity.Usage;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

/// <summary>
/// What a transcript message's turn consumed in total — the assistant's own model turns plus every
/// tool, MCP call and agent underneath them.
/// </summary>
/// <remarks>
/// Unlike the same-named <see cref="ConversationUsage"/> columns (assistant share only), these include
/// tools: a reader sees what the turn cost, and the breakdown lives in the relational audit trail.
/// </remarks>
public sealed record TranscriptMessageUsage
{
    /// <summary>Gets the turn's total input tokens, tools included.</summary>
    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    /// <summary>Gets the turn's total output tokens, tools included.</summary>
    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }
}
