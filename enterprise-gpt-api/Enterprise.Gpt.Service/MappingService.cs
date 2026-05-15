using Microsoft.Extensions.AI;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service
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
