using Enterprise.Gpt.Common.Enums;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// The transcript container holds two kinds of document, told apart by their <c>type</c> property
/// and sharing the <c>(userId, conversationId)</c> partition key.
/// </summary>
public static class CosmosTranscriptTypes
{
    /// <summary>
    /// A conversation's header: its name, counters and dates. One per conversation.
    /// </summary>
    public const string Conversation = "conversation";

    /// <summary>
    /// One message of a conversation.
    /// </summary>
    public const string Message = "message";
}

/// <summary>
/// A conversation's header document, carrying the counters and the name that used to live at the
/// top of a single document containing every message.
/// </summary>
/// <remarks>
/// The identifier is the conversation's own, so the header is reachable by point read rather than
/// by query.
/// </remarks>
public class CosmosConversationHeader
{
    /// <summary>
    /// The current shape of the transcript documents this application writes.
    /// </summary>
    /// <remarks>
    /// Stamped on every document so a future shape change has a discriminator to read rather than
    /// having to infer the version from which properties happen to be present.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = CosmosTranscriptTypes.Conversation;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; set; }

    /// <summary>
    /// How many message positions this conversation has allocated.
    /// </summary>
    /// <remarks>
    /// A stored value rather than a count of anything in memory, because it is also the sequence
    /// allocator: a turn increments it and reads the new value back to learn which positions it
    /// owns. That makes ordering independent of the process-local conversation lock, which gives
    /// no mutual exclusion across replicas.
    /// </remarks>
    [JsonPropertyName("messageCount")]
    public long MessageCount { get; set; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; set; }

    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; set; }

    [JsonPropertyName("dateDeactivated")]
    public DateTimeOffset? DateDeactivated { get; set; }
}

/// <summary>
/// One message of a conversation, stored as its own document.
/// </summary>
/// <remarks>
/// One document per message is what keeps a conversation's length from becoming a storage ceiling:
/// the largest item a conversation produces is bounded by its largest single message rather than by
/// how many messages it holds.
/// </remarks>
public class CosmosConversationMessage
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = CosmosTranscriptTypes.Message;

    /// <summary>
    /// This message's position in the conversation, zero-based.
    /// </summary>
    /// <remarks>
    /// Explicit because separate documents have no array position to lean on, and because a turn's
    /// user and assistant messages share one timestamp — time alone cannot order them. Gaps are
    /// legal: a turn that allocated positions and then failed to write leaves one behind, and
    /// ordering is by ascending sequence rather than by contiguity.
    /// </remarks>
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary>
    /// Who produced the message: <c>system</c>, <c>user</c> or <c>assistant</c>.
    /// </summary>
    /// <remarks>
    /// A string rather than the <see cref="ChatRoles"/> integer, matching the wire contract the
    /// stream already serializes and keeping an exported document readable on its own.
    /// </remarks>
    [JsonPropertyName("role")]
    public string Role { get; set; } = null!;

    [JsonPropertyName("content")]
    public string Content { get; set; } = null!;

    /// <summary>
    /// The message rendered to HTML at persist time, or <see langword="null"/> for a message
    /// written before rendering existed.
    /// </summary>
    /// <remarks>
    /// Stored so an export is a read rather than a re-render. Excluded from the container's
    /// indexing policy alongside <see cref="Content"/>: neither is ever filtered on, and together
    /// they are the bulk of a document's bytes.
    /// </remarks>
    [JsonPropertyName("htmlContent")]
    public string? HtmlContent { get; set; }

    /// <summary>
    /// What this message contributes to the prompt on every future turn.
    /// </summary>
    /// <remarks>
    /// A pure function of the message's own text, and deliberately not what the turn was billed:
    /// an assistant message's tokens count only the transcribed answer, excluding tool-call
    /// arguments the model generated but which are never replayed. Zero for a message written
    /// before estimation existed.
    /// </remarks>
    [JsonPropertyName("tokens")]
    public long Tokens { get; set; }

    /// <summary>
    /// How <see cref="Tokens"/> was arrived at.
    /// </summary>
    [JsonPropertyName("tokenAccuracy")]
    public TokenAccuracies TokenAccuracy { get; set; } = TokenAccuracies.Estimated;

    /// <summary>
    /// The deployment that served the turn, captured as it stood at the time.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("providerId")]
    public Guid? ProviderId { get; set; }

    /// <summary>
    /// What the turn that produced this message was billed, on assistant messages only.
    /// </summary>
    [JsonPropertyName("usage")]
    public CosmosConversationUsage? Usage { get; set; }

    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; set; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CosmosConversationHeader.CurrentSchemaVersion;
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
