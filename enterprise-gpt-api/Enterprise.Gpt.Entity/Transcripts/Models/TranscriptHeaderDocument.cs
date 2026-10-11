using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

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
    /// <para>
    /// Version 2 added <see cref="TranscriptMessageDocument.Attachments"/>. It reads back as empty on
    /// a version 1 document, so nothing migrates and both vintages are readable by the same code.
    /// </para>
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

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

    #region Public static methods

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

    #endregion
}
