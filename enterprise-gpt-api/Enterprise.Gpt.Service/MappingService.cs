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

        /// <summary>
        /// Maps a role to the lowercase name stored on a transcript message.
        /// </summary>
        /// <param name="role">The role to map.</param>
        /// <returns>The stored name, such as <c>assistant</c>.</returns>
        /// <remarks>
        /// A string rather than the enum's number, so an exported transcript stays readable without
        /// the enum to interpret it and so the stored value matches the wire contract the stream
        /// already serializes.
        /// </remarks>
        public static string MapToRoleName(ChatRoles role)
        {
            return role switch
            {
                ChatRoles.System => "system",
                ChatRoles.Assistant => "assistant",
                ChatRoles.User => "user",
                ChatRoles.Tool => "tool",
                _ => throw new ArgumentOutOfRangeException(nameof(role), $"Not expected chat role value: {role}"),
            };
        }

        /// <summary>
        /// Maps a stored role name back to its enum value.
        /// </summary>
        /// <param name="roleName">The name read from a transcript message.</param>
        /// <returns>The matching role.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the name names no known role.</exception>
        public static ChatRoles MapToChatRoles(string roleName)
        {
            return roleName switch
            {
                "system" => ChatRoles.System,
                "assistant" => ChatRoles.Assistant,
                "user" => ChatRoles.User,
                "tool" => ChatRoles.Tool,
                _ => throw new ArgumentOutOfRangeException(nameof(roleName), $"Not expected chat role name: {roleName}"),
            };
        }
    }
}
