using RR.AI_Chat.Dto;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RR.AI_Chat.Entity
{
    [Table(nameof(Conversation), Schema = "Core")]
    public class Conversation : BaseModifiedEntity
    {
        [ForeignKey(nameof(User))]
        public Guid UserId { get; set; }

        [StringLength(256)]
        public string Name { get; set; } = "New Conversation";

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long TotalTokens => InputTokens + OutputTokens;

        public User User { get; set; } = null!;

        public List<ConversationDocument> Documents { get; set; } = [];
    }

    public static class ChatExtensions
    {
        public static ConversationDto MapToChatDto(this Conversation source)
        {
            return new ConversationDto
            {
                Id = source.Id,
                Name = source.Name,
                DateCreated = source.DateCreated,
                DateModified = source.DateModified
            };
        }
    }
}
