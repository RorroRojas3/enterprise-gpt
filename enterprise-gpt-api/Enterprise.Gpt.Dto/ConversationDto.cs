namespace RR.AI_Chat.Dto
{
    public class ConversationDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public DateTimeOffset DateCreated { get; set; }

        public DateTimeOffset DateModified { get; set; }
    }

    public class ChatConversationDto : ConversationDto
    {
        public List<ConversationMessageDto> Messages { get; set; } = [];
    }
}
