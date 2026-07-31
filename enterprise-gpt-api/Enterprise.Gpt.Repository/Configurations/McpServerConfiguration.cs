using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations
{
    public sealed class McpServerConfiguration : IEntityTypeConfiguration<McpServer>
    {
        public void Configure(EntityTypeBuilder<McpServer> builder)
        {
            builder.ToTable("McpServer", "Core.Ref");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.AuthType)
                .HasConversion<int>();

            builder.HasOne(x => x.CreatedBy)
                .WithMany()
                .HasForeignKey(x => x.CreatedById);

            builder.HasOne(x => x.ModifiedBy)
                .WithMany()
                .HasForeignKey(x => x.ModifiedById);

            builder.HasIndex(x => x.Name)
                .HasFilter("[DateDeactivated] IS NULL")
                .IsUnique();
        }
    }
}
