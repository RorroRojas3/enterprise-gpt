using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity.Transcripts;

/// <summary>
/// The <c>type</c> discriminator values that share the transcript container.
/// </summary>
/// <remarks>
/// The container is heterogeneous by design, and this discriminator stays in every read predicate.
/// That is what stops a third document kind, added later, from silently entering queries written
/// before it existed.
/// </remarks>
public static class TranscriptDocumentTypes
{
    /// <summary>A conversation's header document, one per conversation.</summary>
    public const string Conversation = "conversation";

    /// <summary>A single transcript message, one per message.</summary>
    public const string Message = "message";
}

/// <summary>
/// A conversation's header: its name, its counters, and the dates the client reads.
/// </summary>
/// <remarks>
/// One per conversation, stored under the same partition key as the conversation's messages so a
/// turn can write both in one transaction.
/// </remarks>
public sealed record TranscriptHeaderDocument
{
    /// <summary>
    /// The schema version stamped on every document written by this application.
    /// </summary>
    /// <remarks>
    /// Present so a future shape change has a discriminator to read, rather than having to infer a
    /// document's vintage from which properties happen to be absent.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the document identifier, which is the conversation's identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = null!;

    /// <summary>Gets the synthetic partition key, built by <see cref="PartitionKeys.For"/>.</summary>
    [JsonPropertyName("pk")]
    public string Pk { get; init; } = null!;

    /// <summary>Gets the object identifier of the conversation's owner.</summary>
    /// <remarks>
    /// Kept as its own property even though the partition key concatenates it: a document readable
    /// only by parsing its partition key is a document no query and no export can project cleanly.
    /// </remarks>
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    /// <summary>Gets the conversation's identifier.</summary>
    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; init; }

    /// <summary>Gets the document kind discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = TranscriptDocumentTypes.Conversation;

    /// <summary>Gets or sets the conversation's display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets what the conversation's transcript weighs: the sum of its messages' estimated
    /// token costs.
    /// </summary>
    /// <remarks>
    /// The context cost, not the billed one, and the two are <em>supposed</em> to disagree. Tool
    /// calls, tool results and reasoning are billed by the provider and never transcribed, so a
    /// conversation's billed totals in SQL exceed this figure by more the more tools it runs. A
    /// divergence is the model working as designed, not a defect to reconcile.
    /// </remarks>
    [JsonPropertyName("contextTokens")]
    public long ContextTokens { get; set; }

    /// <summary>
    /// Gets or sets how many message documents the conversation has.
    /// </summary>
    /// <remarks>
    /// A settable value rather than a count of an in-memory collection, which is what the previous
    /// shape had while the write path also incremented the stored property — so the two silently
    /// diverged. Exact rather than advisory, because the batch that creates the messages and the
    /// patch that moves this counter are one transaction within the partition.
    /// </remarks>
    [JsonPropertyName("messageCount")]
    public long MessageCount { get; set; }

    /// <summary>Gets the document shape's version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets the moment the conversation was created.</summary>
    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; init; }

    /// <summary>Gets or sets the moment the conversation was last written to.</summary>
    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; set; }

    /// <summary>Gets or sets the moment the conversation was soft-deleted, if it was.</summary>
    [JsonPropertyName("dateDeactivated")]
    public DateTimeOffset? DateDeactivated { get; set; }

    /// <summary>
    /// Creates a header for a new conversation.
    /// </summary>
    /// <param name="userId">The object identifier of the conversation's owner.</param>
    /// <param name="conversationId">The conversation's identifier.</param>
    /// <param name="name">The conversation's display name.</param>
    /// <param name="createdAt">The moment the conversation was created.</param>
    /// <returns>The header, with its partition key already composed.</returns>
    /// <remarks>
    /// The only production construction site, so the partition key cannot be assembled by hand at a
    /// call site that then formats it differently.
    /// </remarks>
    public static TranscriptHeaderDocument Create(Guid userId, Guid conversationId, string name, DateTimeOffset createdAt)
    {
        return new TranscriptHeaderDocument
        {
            Id = conversationId.ToString(),
            Pk = PartitionKeys.For(userId, conversationId),
            UserId = userId,
            ConversationId = conversationId,
            Name = name,
            DateCreated = createdAt,
            DateModified = createdAt
        };
    }
}

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
    /// The rating a reader sees, and the source the transcript response projects from (US-1102).
    /// The relational <c>MessageFeedback</c> row beside it is a projection for reporting, not the
    /// other way round: this is what comes back on a reload, so this is what a rating has to reach.
    /// <para>
    /// Set only on an assistant message — the write path rejects every other role — and written by
    /// a patch long after the document was created, unlike every other property here.
    /// </para>
    /// </remarks>
    [JsonPropertyName("feedback")]
    public TranscriptMessageFeedback? Feedback { get; init; }

    /// <summary>Gets the moment the message was produced.</summary>
    /// <remarks>
    /// The sort key for the whole transcript. A user message is stamped when its prompt was
    /// received and an assistant message when its answer completed — genuinely different moments,
    /// unlike the single timestamp the previous shape stamped on both.
    /// </remarks>
    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; init; }

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
    /// write path, the read path and the purge path.
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
}

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

/// <summary>
/// A reader's verdict on one assistant message (US-1102).
/// </summary>
/// <remarks>
/// Stamped rather than versioned: the transcript is append-only for its messages, but a rating is
/// the one thing about a message that a reader may change afterwards, so the moment recorded here
/// is the moment of the latest change and there is no history of the ones before it. The
/// relational <c>MessageFeedback</c> row keeps its own <c>DateCreated</c> beside this, which is
/// where a first-rated-at ever comes from.
/// </remarks>
public sealed record TranscriptMessageFeedback
{
    /// <summary>Gets the verdict.</summary>
    /// <remarks>
    /// Serialized as a lowercase string here and as PascalCase on the HTTP wire, because the Cosmos
    /// serializer registers a camel-casing string-enum converter and the API deliberately does not
    /// — the same split <see cref="TranscriptMessageDocument.Role"/> already carries.
    /// </remarks>
    [JsonPropertyName("rating")]
    public MessageFeedbackRatings Rating { get; init; }

    /// <summary>Gets the moment the rating was last set.</summary>
    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; init; }
}
