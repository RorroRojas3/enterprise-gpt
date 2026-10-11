namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// The non-generic face of <see cref="BaseCreatedEntity{TKey}"/>.
/// </summary>
public interface IBaseCreatedEntity
{
    /// <summary>When the row was inserted, at offset zero; stamped by the audit interceptor.</summary>
    DateTimeOffset DateCreated { get; set; }
}

/// <summary>
/// A row that is written once and never edited, such as a ledger entry.
/// </summary>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseCreatedEntity<TKey> : BaseEntity<TKey>, IBaseCreatedEntity
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    public DateTimeOffset DateCreated { get; set; }
}
