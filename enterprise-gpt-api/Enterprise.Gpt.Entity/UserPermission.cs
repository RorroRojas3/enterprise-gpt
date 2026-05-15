using RR.AI_Chat.Common.Enums;

namespace RR.AI_Chat.Entity
{
    public class UserPermission : BaseModifiedByEntity
    {
        public Guid UserId { get; set; }

        public Permissions Permission { get; set; }

        public User User { get; set; } = null!;
    }
}
