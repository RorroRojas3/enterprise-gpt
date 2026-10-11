using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// A chunk of a document uploaded into a conversation.
/// </summary>
public class ConversationDocumentChunk : BaseDocumentChunk<Guid>
{
    public Guid ConversationDocumentId { get; set; }

    public ConversationDocument ConversationDocument { get; set; } = null!;
}
