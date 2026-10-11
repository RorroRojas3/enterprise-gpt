using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// Base type for an uploaded or generated document file, shared by the conversation and project families.
/// </summary>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseDocument<TKey> : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    public Guid UserId { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>The lower-cased file extension, including the leading dot.</summary>
    public string Extension { get; set; } = null!;

    public string MimeType { get; set; } = null!;

    /// <summary>The file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>The blob path within its storage container.</summary>
    public string Path { get; set; } = null!;

    public User User { get; set; } = null!;
}
