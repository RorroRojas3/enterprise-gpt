using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ProjectDocumentSummary"/> to <c>Core.ProjectDocumentSummary</c> and the unique
/// index that keeps a document to one live summary.
/// </summary>
public sealed class ProjectDocumentSummaryConfiguration : IEntityTypeConfiguration<ProjectDocumentSummary>
{
    public void Configure(EntityTypeBuilder<ProjectDocumentSummary> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSummary), "Core");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Text)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.HasOne(x => x.ProjectDocument)
            .WithMany()
            .HasForeignKey(x => x.ProjectDocumentId);

        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId);

        // Filtered for the reason the conversation-side index is: a deactivated summary must not
        // block the next one for the same document.
        builder.HasIndex(x => x.ProjectDocumentId)
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
