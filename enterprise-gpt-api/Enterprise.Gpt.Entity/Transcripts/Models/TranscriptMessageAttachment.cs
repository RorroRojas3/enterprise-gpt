using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

/// <summary>
/// One file an assistant message introduced.
/// </summary>
/// <remarks>
/// Copies the identity fields, not just the id, so a reload can draw the chip before fetching anything.
/// </remarks>
public sealed record TranscriptMessageAttachment
{
    /// <summary>Gets the document's identifier, as the download route takes it.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;

    /// <summary>Gets the lower-cased file extension, including the leading dot.</summary>
    [JsonPropertyName("extension")]
    public string Extension { get; init; } = null!;

    /// <summary>Gets the content type derived from the extension.</summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = null!;

    /// <summary>Gets the file size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }
}
