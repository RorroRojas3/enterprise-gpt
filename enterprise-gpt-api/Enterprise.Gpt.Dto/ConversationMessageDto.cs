using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto
{
    public class ConversationMessageDto
    {
        public string Text { get; set; } = null!;

        public ChatRoles Role { get; set; }
    }
}
