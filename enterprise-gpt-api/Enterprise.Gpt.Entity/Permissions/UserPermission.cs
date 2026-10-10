using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Permissions
{
    public class UserPermission : BaseModifiedByEntity
    {
        public Guid UserId { get; set; }

        public Guid PermissionId { get; set; }

        public User User { get; set; } = null!;

        public Permission Permission { get; set; } = null!;
    }
}
