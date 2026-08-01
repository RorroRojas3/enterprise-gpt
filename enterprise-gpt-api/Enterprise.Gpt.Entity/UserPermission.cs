namespace Enterprise.Gpt.Entity
{
    public class UserPermission : BaseModifiedByEntity
    {
        public Guid UserId { get; set; }

        public Guid PermissionId { get; set; }

        public User User { get; set; } = null!;

        public Permission Permission { get; set; } = null!;
    }
}
