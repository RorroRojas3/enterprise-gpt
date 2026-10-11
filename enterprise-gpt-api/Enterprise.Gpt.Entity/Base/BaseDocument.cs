using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// Base type for an uploaded or generated document file, shared by the conversation and project families.
/// </summary>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseDocument<TKey> : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <summary>The user the document belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>The file name as uploaded.</summary>
    public string Name { get; set; } = null!;

    /// <summary>The file extension, without the leading dot.</summary>
    public string Extension { get; set; } = null!;

    /// <summary>The file's media type.</summary>
    public string MimeType { get; set; } = null!;

    /// <summary>The file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>The blob path within its storage container.</summary>
    public string Path { get; set; } = null!;

    public User User { get; set; } = null!;
}
