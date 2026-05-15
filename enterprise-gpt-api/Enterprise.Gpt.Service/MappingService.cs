using Microsoft.Extensions.AI;
using RR.AI_Chat.Common.Enums;

namespace RR.AI_Chat.Service
{
    public static class MappingService
    {
        public static ChatRole MapToChatRole(ChatRoles role)
        {
            return role switch
            {
                ChatRoles.System => ChatRole.System,
                ChatRoles.Assistant => ChatRole.Assistant,
                ChatRoles.User => ChatRole.User,
                ChatRoles.Tool => ChatRole.Tool,
                _ => throw new ArgumentOutOfRangeException(nameof(role), $"Not expected chat role value: {role}"),
            };
        }
    }
}
