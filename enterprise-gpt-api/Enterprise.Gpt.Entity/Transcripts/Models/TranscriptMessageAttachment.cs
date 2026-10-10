using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

/// <summary>
/// One file an assistant message introduced.
/// </summary>
/// <remarks>
/// Deliberately a copy of the identity fields rather than a document id alone: a reload has to draw
/// the chip before anything is fetched, and a name and a size are what it draws.
/// </remarks>
public sealed record TranscriptMessageAttachment
{
    /// <summary>Gets the document's identifier, as the download route takes it.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>Gets the file name to save it under.</summary>
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
