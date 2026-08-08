namespace Enterprise.Gpt.Dto
{
    public class ConversationDto
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The project this conversation belongs to, or <see langword="null"/> when it is standalone.
        /// </summary>
        public Guid? ProjectId { get; set; }

        public string Name { get; set; } = null!;

        public DateTimeOffset DateCreated { get; set; }

        public DateTimeOffset DateModified { get; set; }
    }

    public class ChatConversationDto : ConversationDto
    {
        public List<ConversationMessageDto> Messages { get; set; } = [];
    }
}
