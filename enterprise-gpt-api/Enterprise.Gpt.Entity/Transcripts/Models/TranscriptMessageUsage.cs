using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity.Transcripts;

/// <summary>
/// What a transcript message's turn consumed in total — the assistant's own model turns plus every
/// tool, MCP call and agent underneath them.
/// </summary>
/// <remarks>
/// Deliberately not the same split as the identically named columns on
/// <see cref="ConversationUsage"/>, which hold the assistant's share alone and keep the tool share
/// separate. The transcript is what a reader sees beside a message, and the number that belongs
/// there is what the turn cost; the breakdown lives in the relational audit trail.
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
