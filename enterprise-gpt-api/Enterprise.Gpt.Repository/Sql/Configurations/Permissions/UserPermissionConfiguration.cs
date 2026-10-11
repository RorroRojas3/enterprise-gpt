using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Permissions
{
    public sealed class UserPermissionConfiguration : BaseModifiedByEntityConfiguration<UserPermission, Guid>
    {
        protected override void ConfigureEntity(EntityTypeBuilder<UserPermission> builder)
        {
            builder.ToTable(nameof(UserPermission), "Core");

            builder.HasOne(x => x.User)
                .WithMany(u => u.UserPermissions)
                .HasForeignKey(x => x.UserId);

            builder.HasOne(x => x.Permission)
                .WithMany(p => p.UserPermissions)
                .HasForeignKey(x => x.PermissionId);

            builder.HasIndex(x => new { x.UserId, x.PermissionId })
                .HasFilter("[DateDeactivated] IS NULL")
                .IsUnique();
        }
    }
}
