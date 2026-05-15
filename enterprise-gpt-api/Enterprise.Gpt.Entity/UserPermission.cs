using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity
{
    public class UserPermission : BaseModifiedByEntity
    {
        public Guid UserId { get; set; }

        public Permissions Permission { get; set; }

        public User User { get; set; } = null!;
    }
}
