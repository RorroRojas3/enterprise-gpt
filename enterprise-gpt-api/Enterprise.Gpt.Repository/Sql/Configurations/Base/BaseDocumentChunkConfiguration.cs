using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the text and embedding columns both chunk families share.
/// </summary>
public abstract class BaseDocumentChunkConfiguration<T, TKey> : BaseModifiedEntityConfiguration<T, TKey>
    where T : BaseDocumentChunk<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.Property(e => e.Text).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(e => e.Embedding).HasColumnType($"vector({DocumentChunkLimits.EmbeddingDimensions})");
    }
}
