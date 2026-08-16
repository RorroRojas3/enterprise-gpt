namespace Enterprise.Gpt.Entity.Transcripts;

/// <summary>
/// Builds the synthetic partition key value every transcript document is stored under.
/// </summary>
/// <remarks>
/// <para>
/// The only place in the solution that composes this value. The write path, the read path and the
/// purge path must all address the same partition, and a format assembled at three call sites is a
/// format that drifts at one of them — which would strand a conversation's documents in a partition
/// nothing queries, silently and without an error.
/// </para>
/// <para>
/// Composing both identifiers into one key rather than partitioning on the user is what keeps the
/// tenancy guarantee in storage: a conversation id belonging to another user produces a key
/// addressing a partition that holds nothing, so a foreign transcript is unreadable even if a
/// code-level ownership check is ever missed.
/// </para>
/// <para>
/// A single synthetic key rather than a hierarchical <c>/userId</c> then <c>/conversationId</c>
/// key, which is the obvious alternative and is deliberately rejected: a hierarchical key
/// co-locates every document sharing its first level on one physical partition, so a heavy user's
/// whole history would land on a single partition capped at 10,000 RU/s. That trade buys cheap
/// <c>WHERE userId = @x</c> prefix queries, and this application never issues one — every by-user
/// question is answered from SQL, and Cosmos is only ever asked about one conversation at a time.
/// </para>
/// </remarks>
public static class PartitionKeys
{
    /// <summary>
    /// The path the transcript container is partitioned on.
    /// </summary>
    public const string Path = "/pk";

    /// <summary>
    /// Builds the partition key value for a conversation's documents.
    /// </summary>
    /// <param name="userId">The object identifier of the conversation's owner.</param>
    /// <param name="conversationId">The conversation's unique identifier.</param>
    /// <returns>The value stored on every document's <c>pk</c> property.</returns>
    /// <example>
    /// <code language="csharp">
    /// var pk = PartitionKeys.For(userId, conversationId);
    /// var page = await store.QueryAsync&lt;TranscriptMessageDocument&gt;(query, new PartitionKey(pk), …);
    /// </code>
    /// </example>
    public static string For(Guid userId, Guid conversationId)
    {
        return $"{userId}:{conversationId}";
    }
}
