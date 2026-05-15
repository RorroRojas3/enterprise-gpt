using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RR.AI_Chat.Entity;

namespace RR.AI_Chat.Repository.Configurations
{
    public sealed class UserPermissionConfiguration : IEntityTypeConfiguration<UserPermission>
    {
        public void Configure(EntityTypeBuilder<UserPermission> builder)
        {
            builder.ToTable("UserPermission", "Core");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Permission)
                .HasConversion<int>();

            builder.HasOne(x => x.User)
                .WithMany(u => u.UserPermissions)
                .HasForeignKey(x => x.UserId);

            builder.HasOne(x => x.CreatedBy)
                .WithMany()
                .HasForeignKey(x => x.CreatedById);

            builder.HasOne(x => x.ModifiedBy)
                .WithMany()
                .HasForeignKey(x => x.ModifiedById);

            builder.HasIndex(x => new { x.UserId, x.Permission })
                .HasFilter("[DateDeactivated] IS NULL")
                .IsUnique();
        }
    }
}
