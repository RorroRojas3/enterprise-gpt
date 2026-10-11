using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

/// <summary>
/// A single message in a conversation's transcript.
/// </summary>
/// <remarks>
/// No sequence field: ordering is <c>dateCreated</c> alone, and the write clamps the assistant message to
/// at least one tick after the user's.
/// </remarks>
public sealed record TranscriptMessageDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = null!;

    /// <summary>Gets the synthetic partition key, built by <see cref="PartitionKeys.For"/>.</summary>
    [JsonPropertyName("pk")]
    public string Pk { get; init; } = null!;

    /// <summary>Gets the object identifier of the conversation's owner.</summary>
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; init; }

    /// <summary>Gets the document kind discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = TranscriptDocumentTypes.Message;

    /// <remarks>
    /// Serialized as a string, matching the streaming contract and keeping exports readable.
    /// </remarks>
    [JsonPropertyName("role")]
    public ChatRoles Role { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Gets the message rendered to HTML at persist time, or <see langword="null"/> when rendering
    /// was unavailable.
    /// </summary>
    /// <remarks>
    /// For export fidelity; not byte-identical to the client's own markdown rendering.
    /// </remarks>
    [JsonPropertyName("htmlContent")]
    public string? HtmlContent { get; init; }

    /// <summary>
    /// Gets what this message contributes to the prompt on every future turn.
    /// </summary>
    /// <remarks>
    /// A pure function of the message text. Excludes tool spend, since only the answer text is replayed,
    /// so it will not equal <c>usage.outputTokens</c>.
    /// </remarks>
    [JsonPropertyName("tokens")]
    public long Tokens { get; init; }

    /// <summary>Gets how <see cref="Tokens"/> was arrived at.</summary>
    [JsonPropertyName("tokenAccuracy")]
    public TokenAccuracies TokenAccuracy { get; init; } = TokenAccuracies.Estimated;

    /// <summary>
    /// Gets the deployment that served the message, or <see langword="null"/> for a message no
    /// model produced.
    /// </summary>
    /// <remarks>
    /// The deployment name, not the display label, so this append-only record survives a model rename.
    /// </remarks>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>
    /// Gets what the turn that produced this message was billed, or <see langword="null"/> on any
    /// message the provider did not bill for.
    /// </summary>
    [JsonPropertyName("usage")]
    public TranscriptMessageUsage? Usage { get; init; }

    /// <summary>
    /// Gets the reader's verdict on this message, or <see langword="null"/> when it has never
    /// been rated, or the rating was withdrawn.
    /// </summary>
    /// <remarks>
    /// The source of truth a reload reads; the relational <c>MessageFeedback</c> row is a reporting
    /// projection. Assistant messages only, and patched long after the document was created.
    /// </remarks>
    [JsonPropertyName("feedback")]
    public TranscriptMessageFeedback? Feedback { get; init; }

    /// <summary>
    /// Gets the files this message introduced, or an empty list when it introduced none.
    /// </summary>
    /// <remarks>
    /// Identities, never credentials: download links are minted on click. Older messages deserialize to
    /// an empty list, so no migration runs.
    /// </remarks>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<TranscriptMessageAttachment> Attachments { get; init; } = [];

    /// <remarks>
    /// The transcript's sort key: a user message is stamped on receipt, an assistant message on completion.
    /// </remarks>
    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; init; }

    #region Public static methods

    /// <summary>
    /// Creates a message document for a conversation.
    /// </summary>
    /// <remarks>
    /// The only production construction site, so the partition key format cannot drift between the
    /// write path and the read path.
    /// </remarks>
    public static TranscriptMessageDocument Create(
        Guid userId,
        Guid conversationId,
        Guid messageId,
        ChatRoles role,
        string content,
        DateTimeOffset createdAt)
    {
        return new TranscriptMessageDocument
        {
            Id = messageId.ToString(),
            Pk = PartitionKeys.For(userId, conversationId),
            UserId = userId,
            ConversationId = conversationId,
            Role = role,
            Content = content,
            DateCreated = createdAt
        };
    }

    #endregion
}
