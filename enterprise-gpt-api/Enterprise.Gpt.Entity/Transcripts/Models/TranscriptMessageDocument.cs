using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

/// <summary>
/// A single message in a conversation's transcript.
/// </summary>
/// <remarks>
/// Carries no sequence field of any kind: ordering is <c>dateCreated</c> alone, which is legitimate
/// because a turn's user and assistant messages are stamped at genuinely different moments and the
/// write clamps the second to at least one tick after the first.
/// </remarks>
public sealed record TranscriptMessageDocument
{
    /// <summary>Gets the message's identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = null!;

    /// <summary>Gets the synthetic partition key, built by <see cref="PartitionKeys.For"/>.</summary>
    [JsonPropertyName("pk")]
    public string Pk { get; init; } = null!;

    /// <summary>Gets the object identifier of the conversation's owner.</summary>
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    /// <summary>Gets the conversation the message belongs to.</summary>
    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; init; }

    /// <summary>Gets the document kind discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = TranscriptDocumentTypes.Message;

    /// <summary>Gets who produced the message.</summary>
    /// <remarks>
    /// Serialized as a string rather than the enum's integer, matching the streaming contract and
    /// keeping an exported document readable.
    /// </remarks>
    [JsonPropertyName("role")]
    public ChatRoles Role { get; init; }

    /// <summary>Gets the message's text.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Gets the message rendered to HTML at persist time, or <see langword="null"/> when rendering
    /// was unavailable.
    /// </summary>
    /// <remarks>
    /// An export-fidelity artefact rather than a byte-identical mirror of what the client shows:
    /// the client renders through its own markdown stack, and the two differ in whitespace and
    /// syntax-highlighting markup.
    /// </remarks>
    [JsonPropertyName("htmlContent")]
    public string? HtmlContent { get; init; }

    /// <summary>
    /// Gets what this message contributes to the prompt on every future turn.
    /// </summary>
    /// <remarks>
    /// A pure function of the message's own text, so it is deterministic, recomputable and
    /// comparable across roles. On an assistant message it deliberately excludes tool spend: only
    /// the answer text is transcribed and replayed, so folding in tool tokens would describe a
    /// prompt that never gets sent. It will therefore not equal <c>usage.outputTokens</c>, and that
    /// is correct.
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
    /// The deployment name rather than the catalog's display label: the transcript is append-only,
    /// so the value recorded here has to keep its meaning after a model is renamed, and it is the
    /// deployment that identifies what actually served and billed the turn.
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
    /// The rating a reader sees, and the source the transcript response projects from.
    /// The relational <c>MessageFeedback</c> row beside it is a projection for reporting, not the
    /// other way round: this is what comes back on a reload, so this is what a rating has to reach.
    /// <para>
    /// Set only on an assistant message — the write path rejects every other role — and written by
    /// a patch long after the document was created, unlike every other property here.
    /// </para>
    /// </remarks>
    [JsonPropertyName("feedback")]
    public TranscriptMessageFeedback? Feedback { get; init; }

    /// <summary>
    /// Gets the files this message introduced, or an empty list when it introduced none.
    /// </summary>
    /// <remarks>
    /// Identities and never credentials: a download link is minted on click, so a transcript that
    /// leaked would carry nothing usable. A message written before this field existed deserializes to
    /// an empty list, which is why no transcript migration runs for it.
    /// </remarks>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<TranscriptMessageAttachment> Attachments { get; init; } = [];

    /// <summary>Gets the moment the message was produced.</summary>
    /// <remarks>
    /// The sort key for the whole transcript. A user message is stamped when its prompt was
    /// received and an assistant message when its answer completed — genuinely different moments,
    /// unlike the single timestamp the previous shape stamped on both.
    /// </remarks>
    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; init; }

    #region Public static methods

    /// <summary>
    /// Creates a message document for a conversation.
    /// </summary>
    /// <param name="userId">The object identifier of the conversation's owner.</param>
    /// <param name="conversationId">The conversation the message belongs to.</param>
    /// <param name="messageId">The message's identifier.</param>
    /// <param name="role">Who produced the message.</param>
    /// <param name="content">The message's text.</param>
    /// <param name="createdAt">The moment the message was produced.</param>
    /// <returns>The message, with its partition key already composed.</returns>
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
