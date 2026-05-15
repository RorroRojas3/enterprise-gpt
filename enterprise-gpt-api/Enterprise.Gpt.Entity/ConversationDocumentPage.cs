using System.ComponentModel.DataAnnotations.Schema;

namespace RR.AI_Chat.Entity
{
    [Table(nameof(ConversationDocumentPage), Schema = "Core")]
    public class ConversationDocumentPage : BaseDocumentPage
    {
        public Guid ConversationDocumentId { get; set; }

        public ConversationDocument ConversationDocument { get; set; } = null!;
    }
}
