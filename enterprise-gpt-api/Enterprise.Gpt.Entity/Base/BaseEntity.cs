namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// The non-generic face of <see cref="BaseEntity{TKey}"/>, so the interceptors can reach every key type at once.
/// </summary>
public interface IBaseEntity
{
    /// <summary>When the row was soft-deleted, at offset zero; <see langword="null"/> while it is live.</summary>
    DateTimeOffset? DateDeactivated { get; set; }
}

/// <summary>
/// The key and soft-delete stamp every table carries.
/// </summary>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseEntity<TKey> : IBaseEntity
    where TKey : struct, IEquatable<TKey>
{
    public TKey Id { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? DateDeactivated { get; set; }
}
