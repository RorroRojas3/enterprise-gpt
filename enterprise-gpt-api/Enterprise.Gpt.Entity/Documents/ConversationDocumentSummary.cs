using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

/// <summary>
/// The canonical summary of a document uploaded into a conversation.
/// </summary>
public class ConversationDocumentSummary : BaseDocumentSummary<Guid>
{
    /// <summary>The document this summarizes.</summary>
    public Guid ConversationDocumentId { get; set; }

    public ConversationDocument ConversationDocument { get; set; } = null!;
}
