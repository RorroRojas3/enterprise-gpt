using Enterprise.Gpt.Common.Constants.McpServers;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.McpServers;

/// <summary>
/// Maps <see cref="UserMcpCredential"/> to <c>Core.UserMcpCredential</c> and the unique index that
/// keeps a user to one live credential per server.
/// </summary>
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

        // Also the covering index for the lookup on the tool-acquisition path, which reads by
        // both columns. Filtered like every other unique index here: a credential deactivated
        // alongside its server must not block the user supplying a new one later.
        builder.HasIndex(x => new { x.UserId, x.McpServerId })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
