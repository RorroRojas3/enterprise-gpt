using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

/// <summary>
/// Maps <see cref="ProjectDocumentSummary"/> to <c>Core.ProjectDocumentSummary</c> and the unique
/// index that keeps a document to one live summary.
/// </summary>
public sealed class ProjectDocumentSummaryConfiguration : BaseDocumentSummaryConfiguration<ProjectDocumentSummary, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectDocumentSummary> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSummary), "Core");

        builder.HasOne(x => x.ProjectDocument)
            .WithMany()
            .HasForeignKey(x => x.ProjectDocumentId);

        // Filtered for the reason the conversation-side index is: a deactivated summary must not
        // block the next one for the same document.
        builder.HasIndex(x => x.ProjectDocumentId)
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
