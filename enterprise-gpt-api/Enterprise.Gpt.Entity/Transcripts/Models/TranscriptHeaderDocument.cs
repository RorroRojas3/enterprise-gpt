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
    /// Version 2 added <see cref="TranscriptMessageDocument.Attachments"/>, which reads back empty on
    /// version 1 documents, so nothing migrates.
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
    /// Stored separately although the partition key embeds it, so queries and exports can project it.
    /// </remarks>
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; init; }

    /// <summary>Gets the document kind discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = TranscriptDocumentTypes.Conversation;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets what the conversation's transcript weighs: the sum of its messages' estimated
    /// token costs.
    /// </summary>
    /// <remarks>
    /// Context cost, not billed cost: tool calls, tool results and reasoning are billed but never
    /// transcribed, so the SQL totals exceed this by design.
    /// </remarks>
    [JsonPropertyName("contextTokens")]
    public long ContextTokens { get; set; }

    /// <remarks>
    /// Exact: the batch that creates the messages and the patch that moves this counter are one
    /// transaction within the partition.
    /// </remarks>
    [JsonPropertyName("messageCount")]
    public long MessageCount { get; set; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; init; }

    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; set; }

    /// <summary>Gets or sets the moment the conversation was soft-deleted, if it was.</summary>
    [JsonPropertyName("dateDeactivated")]
    public DateTimeOffset? DateDeactivated { get; set; }

    #region Public static methods

    /// <summary>
    /// Creates a header for a new conversation.
    /// </summary>
    /// <remarks>
    /// The only production construction site, so no call site assembles the partition key by hand.
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
