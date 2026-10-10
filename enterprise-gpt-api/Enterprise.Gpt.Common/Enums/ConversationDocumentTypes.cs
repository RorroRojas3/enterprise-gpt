namespace Enterprise.Gpt.Dto.Enums;

/// <summary>
/// How a <c>Core.ConversationDocument</c> row came into existence.
/// </summary>
/// <remarks>
/// Numbered from 1 so an unset column is distinguishable from a legitimate value, and append-only:
/// the numbers are an on-disk contract, so a new kind is added rather than renumbering these.
/// </remarks>
public enum ConversationDocumentTypes
{
    /// <summary>A file a user uploaded into the conversation. Ingested, chunked, retrievable.</summary>
    Uploaded = 1,

    /// <summary>
    /// A file the assistant produced. Never ingested, so it has no chunk rows and is structurally
    /// absent from retrieval, and it lives in its own blob container.
    /// </summary>
    Generated = 2
}
