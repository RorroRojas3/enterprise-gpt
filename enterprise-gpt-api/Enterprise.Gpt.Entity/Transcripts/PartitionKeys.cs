namespace Enterprise.Gpt.Entity.Transcripts;

/// <summary>
/// Builds the synthetic partition key value every transcript document is stored under.
/// </summary>
/// <remarks>
/// The only place this is composed: a format drifting between write and read paths would silently strand
/// documents. Both ids are composed so another user's conversation id addresses an empty partition, keeping
/// tenancy in storage. Not hierarchical (<c>/userId</c>, <c>/conversationId</c>): that would pin a heavy
/// user to one 10,000 RU/s partition to buy by-user prefix queries this app never issues.
/// </remarks>
public static class PartitionKeys
{
    /// <summary>
    /// The path the transcript container is partitioned on.
    /// </summary>
    public const string Path = "/pk";

    #region Public static methods

    /// <summary>
    /// Builds the partition key value for a conversation's documents.
    /// </summary>
    public static string For(Guid userId, Guid conversationId)
    {
        return $"{userId}:{conversationId}";
    }

    #endregion
}
