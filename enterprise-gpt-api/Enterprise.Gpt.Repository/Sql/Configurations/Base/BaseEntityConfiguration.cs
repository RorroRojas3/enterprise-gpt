using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Repository.Sql.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the key every <see cref="BaseEntity{TKey}"/> carries, then hands the rest to <see cref="ConfigureEntity"/>.
/// </summary>
public abstract class BaseEntityConfiguration<T, TKey> : IEntityTypeConfiguration<T>
    where T : BaseEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<T> builder)
    {
        ConfigureBase(builder);
        ConfigureEntity(builder);
    }

    /// <summary>Whether the <see cref="QueryFilterNames.SoftDelete"/> filter hides this entity's deactivated rows.</summary>
    /// <remarks>
    /// Off for a principal that history and reports reach through a required navigation, which a filtered
    /// principal would turn into an inner join that drops the history row.
    /// </remarks>
    protected virtual bool IsSoftDeleteFiltered => true;

    /// <summary>Maps the members the base classes declare.</summary>
    protected virtual void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        if (IsSoftDeleteFiltered)
        {
            builder.HasQueryFilter(QueryFilterNames.SoftDelete, e => e.DateDeactivated == null);
        }
    }

    /// <summary>Maps the members the entity itself declares.</summary>
    protected abstract void ConfigureEntity(EntityTypeBuilder<T> builder);
}
