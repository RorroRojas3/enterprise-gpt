using Enterprise.Gpt.Dto;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

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

        /// <summary>
        /// The model this conversation's most recent turn ran with, or <see langword="null"/> before
        /// its first turn. Denormalized from the newest <see cref="ConversationUsage"/> row so
        /// restoring the model selection does not have to read the audit trail. Set for an abandoned
        /// turn too — it is still the model the user last chose.
        /// </summary>
        public Guid? ModelId { get; set; }

        [StringLength(256)]
        public string Name { get; set; } = "New Conversation";

        public bool IsFavorite { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long TotalTokens => InputTokens + OutputTokens;

        public User User { get; set; } = null!;

        public Project? Project { get; set; }

        public Model? Model { get; set; }

        public List<ConversationDocument> Documents { get; set; } = [];

        public List<ConversationUsage> Usage { get; set; } = [];
    }

    public static class ChatExtensions
    {
        public static ConversationDto MapToChatDto(this Conversation source)
        {
            return new ConversationDto
            {
                Id = source.Id,
                ProjectId = source.ProjectId,
                ModelId = source.ModelId,
                Name = source.Name,
                IsFavorite = source.IsFavorite,
                DateCreated = source.DateCreated,
                DateModified = source.DateModified
            };
        }

        /// <summary>
        /// LINQ projection from <see cref="Conversation"/> to <see cref="ConversationDto"/> for use
        /// inside <c>IQueryable.Select(...)</c>.
        /// </summary>
        /// <remarks>
        /// Kept alongside <see cref="MapToChatDto"/> rather than replacing it: a query that calls the
        /// method in a top-level projection is client-evaluated, so it materializes whole entities —
        /// including the rowversion and the token counters — to build a five-field DTO.
        /// </remarks>
        public static Expression<Func<Conversation, ConversationDto>> MapToChatDtoExpression { get; } =
            source => new ConversationDto
            {
                Id = source.Id,
                ProjectId = source.ProjectId,
                ModelId = source.ModelId,
                Name = source.Name,
                IsFavorite = source.IsFavorite,
                DateCreated = source.DateCreated,
                DateModified = source.DateModified
            };
    }
}
