using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Permissions;

namespace Enterprise.Gpt.Entity.Users;

public class User : BaseModifiedEntity<Guid>
{
    public string FirstName { get; set; } = null!;

    public string LastName { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string FullName => $"{FirstName} {LastName}";

    public ICollection<UserPermission> UserPermissions { get; set; } = [];
}
