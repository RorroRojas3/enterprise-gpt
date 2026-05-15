using RR.AI_Chat.Common.Enums;

namespace RR.AI_Chat.Dto
{
    public class ConversationMessageDto
    {
        public string Text { get; set; } = null!;

        public ChatRoles Role { get; set; }
    }
}
