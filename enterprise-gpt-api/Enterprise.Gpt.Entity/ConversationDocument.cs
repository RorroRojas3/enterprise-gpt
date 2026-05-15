using Enterprise.Gpt.Dto;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(ConversationDocument), Schema = "Core")]
    public class ConversationDocument : BaseDocument
    {
        public Guid ConversationId { get; set; }

        public Conversation Conversation { get; set; } = null!;

        public List<ConversationDocumentPage> Pages { get; set; } = [];
    }

    public static class ChatDocumentExtensions
    {
        public static ConversationDocumentDto MapToChatDocumentDto(this ConversationDocument source)
        {
            return new ConversationDocumentDto
            {
                Id = source.Id,
                ConversationId = source.ConversationId,
                Name = source.Name
            };
        }
    }
}
