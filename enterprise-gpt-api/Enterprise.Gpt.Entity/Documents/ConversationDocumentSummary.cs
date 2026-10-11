using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

public class ConversationDocumentSummary : BaseDocumentSummary<Guid>
{
    public Guid ConversationDocumentId { get; set; }

    public ConversationDocument ConversationDocument { get; set; } = null!;
}
