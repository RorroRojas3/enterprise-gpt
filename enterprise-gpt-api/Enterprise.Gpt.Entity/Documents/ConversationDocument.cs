using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Conversations;

namespace Enterprise.Gpt.Entity.Documents;

public class ConversationDocument : BaseDocument<Guid>
{
    public Guid ConversationId { get; set; }

    public ConversationDocumentTypes Type { get; set; } = ConversationDocumentTypes.Uploaded;

    public Conversation Conversation { get; set; } = null!;

    public List<ConversationDocumentChunk> Chunks { get; set; } = [];

    public List<ConversationDocumentSheet> Sheets { get; set; } = [];
}
