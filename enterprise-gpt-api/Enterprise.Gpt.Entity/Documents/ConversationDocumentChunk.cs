using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Documents;

public class ConversationDocumentChunk : BaseDocumentChunk<Guid>
{
    public Guid ConversationDocumentId { get; set; }

    public ConversationDocument ConversationDocument { get; set; } = null!;
}
