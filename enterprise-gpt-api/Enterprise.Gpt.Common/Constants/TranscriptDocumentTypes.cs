namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// The <c>type</c> discriminator values that share the transcript container.
/// </summary>
/// <remarks>
/// Every read predicate keeps the discriminator, so a document kind added later cannot silently enter
/// existing queries.
/// </remarks>
public static class TranscriptDocumentTypes
{
    /// <summary>A conversation's header document, one per conversation.</summary>
    public const string Conversation = "conversation";

    public const string Message = "message";
}
