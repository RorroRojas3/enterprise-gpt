using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the worksheet columns both sheet families share.
/// </summary>
public abstract class BaseDocumentSheetConfiguration<T, TKey> : BaseModifiedEntityConfiguration<T, TKey>
    where T : BaseDocumentSheet<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.Property(e => e.SheetName).HasMaxLength(DocumentSheetLimits.SheetNameMaxLength);
    }
}
