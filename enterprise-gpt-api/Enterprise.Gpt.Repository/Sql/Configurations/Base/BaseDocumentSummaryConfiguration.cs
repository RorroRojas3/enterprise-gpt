using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the summary text and the model snapshot both summary families share.
/// </summary>
public abstract class BaseDocumentSummaryConfiguration<T, TKey> : BaseModifiedEntityConfiguration<T, TKey>
    where T : BaseDocumentSummary<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.Property(e => e.Text).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(e => e.DeploymentName).HasMaxLength(DocumentSummaryLimits.DeploymentNameMaxLength);
        builder.Property(e => e.PromptVersion).HasMaxLength(DocumentSummaryLimits.PromptVersionMaxLength);
        builder.HasOne(e => e.Model).WithMany().HasForeignKey(e => e.ModelId);
    }
}
