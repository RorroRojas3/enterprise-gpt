using Enterprise.Gpt.Common.Enums;
using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Service.Conversations;

public static class ChatRoleMapper
{
    #region Public static methods

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

    #endregion
}
