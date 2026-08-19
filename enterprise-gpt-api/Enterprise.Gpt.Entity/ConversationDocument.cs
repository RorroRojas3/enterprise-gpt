using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(ConversationDocument), Schema = "Core")]
    public class ConversationDocument : BaseDocument
    {
        public Guid ConversationId { get; set; }

        public Conversation Conversation { get; set; } = null!;

        public List<ConversationDocumentChunk> Chunks { get; set; } = [];
    }
}
