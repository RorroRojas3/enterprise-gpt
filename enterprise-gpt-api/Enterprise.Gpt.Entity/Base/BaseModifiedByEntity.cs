using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// An administrator-maintained row that records who created it and who last changed it.
/// </summary>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseModifiedByEntity<TKey> : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    public Guid CreatedById { get; set; }

    public Guid ModifiedById { get; set; }

    public User CreatedBy { get; set; } = null!;

    public User ModifiedBy { get; set; } = null!;
}
