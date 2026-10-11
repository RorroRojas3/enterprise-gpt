using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the column-name and inferred-type columns both sheet-column families share.
/// </summary>
public abstract class BaseDocumentSheetColumnConfiguration<T, TKey> : BaseModifiedEntityConfiguration<T, TKey>
    where T : BaseDocumentSheetColumn<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.Property(e => e.ColumnName).HasMaxLength(DocumentSheetColumnLimits.ColumnNameMaxLength);
        builder.Property(e => e.InferredType).HasConversion<int>();
    }
}
