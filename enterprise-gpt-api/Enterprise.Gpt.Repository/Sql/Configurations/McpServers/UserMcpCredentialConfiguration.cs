using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="UserMcpCredential"/> to <c>Core.UserMcpCredential</c> and the unique index that
/// keeps a user to one live credential per server.
/// </summary>
public sealed class UserMcpCredentialConfiguration : IEntityTypeConfiguration<UserMcpCredential>
{
    public void Configure(EntityTypeBuilder<UserMcpCredential> builder)
    {
        builder.ToTable(nameof(UserMcpCredential), "Core");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Ciphertext)
            .IsRequired();

        builder.Property(x => x.ApiKeyHint)
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasOne(x => x.McpServer)
            .WithMany(x => x.UserCredentials)
            .HasForeignKey(x => x.McpServerId);

        builder.HasOne(x => x.CreatedBy)
            .WithMany()
            .HasForeignKey(x => x.CreatedById);

        builder.HasOne(x => x.ModifiedBy)
            .WithMany()
            .HasForeignKey(x => x.ModifiedById);

        // Also the covering index for the lookup on the tool-acquisition path, which reads by
        // both columns. Filtered like every other unique index here: a credential deactivated
        // alongside its server must not block the user supplying a new one later.
        builder.HasIndex(x => new { x.UserId, x.McpServerId })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
