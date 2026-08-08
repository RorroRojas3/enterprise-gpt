using Enterprise.Gpt.Dto;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    [Table(nameof(Conversation), Schema = "Core")]
    public class Conversation : BaseModifiedEntity
    {
        [ForeignKey(nameof(User))]
        public Guid UserId { get; set; }

        /// <summary>
        /// The project this conversation belongs to, or <see langword="null"/> for a standalone
        /// conversation. Cleared rather than cascaded when the project is deactivated, so the
        /// conversation and its own documents survive.
        /// </summary>
        public Guid? ProjectId { get; set; }

        [StringLength(256)]
        public string Name { get; set; } = "New Conversation";

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long TotalTokens => InputTokens + OutputTokens;

        public User User { get; set; } = null!;

        public Project? Project { get; set; }

        public List<ConversationDocument> Documents { get; set; } = [];
    }

    public static class ChatExtensions
    {
        public static ConversationDto MapToChatDto(this Conversation source)
        {
            return new ConversationDto
            {
                Id = source.Id,
                ProjectId = source.ProjectId,
                Name = source.Name,
                DateCreated = source.DateCreated,
                DateModified = source.DateModified
            };
        }
    }
}
