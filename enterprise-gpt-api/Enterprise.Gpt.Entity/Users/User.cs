using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Permissions;

namespace Enterprise.Gpt.Entity.Users
{
    [Table(nameof(User), Schema = "Core")]
    public class User : BaseModifiedEntity
    {
        [StringLength(256)]
        public string FirstName { get; set; } = null!;

        [StringLength(256)]
        public string LastName { get; set; } = null!;

        [StringLength(512)]
        [EmailAddress]
        public string Email { get; set; } = null!;

        [NotMapped]
        public string FullName => $"{FirstName} {LastName}";

        public ICollection<UserPermission> UserPermissions { get; set; } = [];
    }
}
