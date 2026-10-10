using System.ComponentModel.DataAnnotations.Schema;
using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(ConversationDocument), Schema = "Core")]
    public class ConversationDocument : BaseDocument
    {
        public Guid ConversationId { get; set; }

        public ConversationDocumentTypes Type { get; set; } = ConversationDocumentTypes.Uploaded;

        public Conversation Conversation { get; set; } = null!;

        public List<ConversationDocumentChunk> Chunks { get; set; } = [];

        public List<ConversationDocumentSheet> Sheets { get; set; } = [];
    }
}
