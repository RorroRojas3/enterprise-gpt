using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps <see cref="BaseCreatedEntity{TKey}.DateCreated"/>.
/// </summary>
public abstract class BaseCreatedEntityConfiguration<T, TKey> : BaseEntityConfiguration<T, TKey>
    where T : BaseCreatedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.Property(e => e.DateCreated).IsRequired();
    }
}
