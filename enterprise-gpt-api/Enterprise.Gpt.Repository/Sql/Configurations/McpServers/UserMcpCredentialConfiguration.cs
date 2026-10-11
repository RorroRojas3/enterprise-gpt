using Enterprise.Gpt.Common.Constants.McpServers;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.McpServers;

public sealed class UserMcpCredentialConfiguration : BaseModifiedByEntityConfiguration<UserMcpCredential, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<UserMcpCredential> builder)
    {
        builder.ToTable(nameof(UserMcpCredential), "Core");

        builder.Property(x => x.Ciphertext)
            .HasMaxLength(UserMcpCredentialLimits.CiphertextMaxLength)
            .IsRequired();

        builder.Property(x => x.ApiKeyHint)
            .HasMaxLength(UserMcpCredentialLimits.ApiKeyHintMaxLength)
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasOne(x => x.McpServer)
            .WithMany(x => x.UserCredentials)
            .HasForeignKey(x => x.McpServerId);

        // Also covers the tool-acquisition lookup. Filtered so a credential deactivated with its server
        // does not block a new one.
        builder.HasIndex(x => new { x.UserId, x.McpServerId })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
