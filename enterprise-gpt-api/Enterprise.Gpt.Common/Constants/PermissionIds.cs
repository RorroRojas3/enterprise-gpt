using System.Collections.Frozen;

namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// Contains the fixed identifiers of built-in permissions seeded by the database model.
/// </summary>
/// <remarks>
/// Each id must match its seeded <c>Core.Permission</c> row exactly; out-of-band data migrations use them
/// verbatim.
/// </remarks>
public static class PermissionIds
{
    public static readonly Guid Administrator = new("a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d");

    /// <summary>Seeded with <c>IsDefault</c> set so every user receives it.</summary>
    public static readonly Guid UploadFile = new("b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e");

    /// <summary>Lets the assistant create, edit, compare and convert documents for the holder.</summary>
    /// <remarks>
    /// Neither implied by <see cref="Administrator"/> nor default: each run provisions a billed sandbox.
    /// </remarks>
    public static readonly Guid GenerateFiles = new("c2d3e4f5-a6b7-4c8d-9e0f-1a2b3c4d5e6f");

    /// <summary>
    /// Display names of the built-in permissions, for 403 messages from id-only endpoint filters.
    /// </summary>
    /// <remarks>
    /// A database lookup would reintroduce the query the permission cache avoids. Must stay in step with the
    /// seeded <c>Core.Permission</c> names.
    /// </remarks>
    public static readonly FrozenDictionary<Guid, string> Names = new Dictionary<Guid, string>
    {
        [Administrator] = "Administrator",
        [UploadFile] = "Upload File",
        [GenerateFiles] = "Generate Files"
    }.ToFrozenDictionary();
}
