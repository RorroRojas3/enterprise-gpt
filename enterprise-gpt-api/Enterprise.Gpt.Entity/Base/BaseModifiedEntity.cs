namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// The non-generic face of <see cref="BaseModifiedEntity{TKey}"/>.
/// </summary>
public interface IBaseModifiedEntity
{
    /// <summary>When the row last changed, at offset zero; stamped by the audit interceptor.</summary>
    /// <remarks>Non-null and stamped on insert as well, so "last changed" never falls back to the creation date.</remarks>
    DateTimeOffset DateModified { get; set; }
}

/// <summary>
/// A row that is edited after insert, guarded by a row version.
/// </summary>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseModifiedEntity<TKey> : BaseCreatedEntity<TKey>, IBaseModifiedEntity
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    public DateTimeOffset DateModified { get; set; }

    /// <summary>The concurrency token; <c>rowversion</c> on SQL Server.</summary>
    public byte[] Version { get; set; } = [];
}
