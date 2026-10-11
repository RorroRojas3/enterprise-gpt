using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the cells column both sheet-row families share.
/// </summary>
public abstract class BaseDocumentSheetRowConfiguration<T, TKey> : BaseModifiedEntityConfiguration<T, TKey>
    where T : BaseDocumentSheetRow<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        // Stated: EF infers json only for ToJson complex types and primitive collections, not a hand-serialized string.
        builder.Property(e => e.Cells).HasColumnType("json").IsRequired();
    }
}
