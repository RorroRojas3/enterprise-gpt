using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the creating and last-modifying user of a <see cref="BaseModifiedByEntity{TKey}"/>.
/// </summary>
public abstract class BaseModifiedByEntityConfiguration<T, TKey> : BaseModifiedEntityConfiguration<T, TKey>
    where T : BaseModifiedByEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.HasOne(e => e.CreatedBy).WithMany().HasForeignKey(e => e.CreatedById);
        builder.HasOne(e => e.ModifiedBy).WithMany().HasForeignKey(e => e.ModifiedById);
    }
}
