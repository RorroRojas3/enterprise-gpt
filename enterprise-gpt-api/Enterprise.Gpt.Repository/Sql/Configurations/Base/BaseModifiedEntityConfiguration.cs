using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps <see cref="BaseModifiedEntity{TKey}.DateModified"/> and the row version.
/// </summary>
public abstract class BaseModifiedEntityConfiguration<T, TKey> : BaseCreatedEntityConfiguration<T, TKey>
    where T : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.Property(e => e.DateModified).IsRequired();
        builder.Property(e => e.Version).IsRowVersion();
    }
}
