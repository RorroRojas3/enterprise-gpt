using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    /// <summary>
    /// A chunk of a document uploaded into a conversation.
    /// </summary>
    [Table(nameof(ConversationDocumentChunk), Schema = "Core")]
    public class ConversationDocumentChunk : BaseDocumentChunk
    {
        public Guid ConversationDocumentId { get; set; }

        public ConversationDocument ConversationDocument { get; set; } = null!;
    }
}
