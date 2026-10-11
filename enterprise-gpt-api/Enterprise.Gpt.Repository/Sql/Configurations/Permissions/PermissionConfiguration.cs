using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Constants.Permissions;
using Enterprise.Gpt.Entity.Permissions;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Permissions;

public sealed class PermissionConfiguration : BaseModifiedByEntityConfiguration<Permission, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable(nameof(Permission), "Core");

        builder.Property(x => x.Name).HasMaxLength(PermissionLimits.NameMaxLength);
        builder.Property(x => x.Description).HasMaxLength(PermissionLimits.DescriptionMaxLength);

        builder.HasOne(x => x.McpServer)
            .WithMany(s => s.Permissions)
            .HasForeignKey(x => x.McpServerId);

        builder.HasIndex(x => x.Name)
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();

        builder.HasIndex(x => x.McpServerId);

        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var userId = Guid.Parse("5f7ab694-1b6c-4b19-badd-c82b65e794cf");
        builder.HasData(
            new Permission
            {
                Id = PermissionIds.Administrator,
                Name = "Administrator",
                Description = "Full administrative access.",
                IsDefault = false,
                DateCreated = date,
                DateModified = date,
                CreatedById = userId,
                ModifiedById = userId
            },
            // IsDefault is what grants this to every user at provisioning time; UserService also
            // reconciles missing default grants on sign-in, so users created before this row existed
            // pick it up too.
            new Permission
            {
                Id = PermissionIds.UploadFile,
                Name = "Upload File",
                Description = "Upload documents into a conversation or a project.",
                IsDefault = true,
                DateCreated = date,
                DateModified = date,
                CreatedById = userId,
                ModifiedById = userId
            },
            // Not IsDefault, unlike the upload grant beside it: every run this permits provisions a
            // billed sandbox session, so it is granted deliberately rather than handed to everyone.
            new Permission
            {
                Id = PermissionIds.GenerateFiles,
                Name = "Generate Files",
                Description = "Ask the assistant to create, edit, compare and convert documents.",
                IsDefault = false,
                DateCreated = date,
                DateModified = date,
                CreatedById = userId,
                ModifiedById = userId
            }
        );
    }
}
