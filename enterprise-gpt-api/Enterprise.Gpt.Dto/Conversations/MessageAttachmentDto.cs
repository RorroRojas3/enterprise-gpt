using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Conversations;

/// <summary>
/// The identity of a file introduced by an assistant message, as carried on the transcript and
/// returned to a client.
/// </summary>
/// <remarks>
/// Never a credential: download links are minted on click. The <see cref="JsonPropertyNameAttribute"/>s
/// also camelCase this type on the streamed turn's metadata, which is serialized with default options.
/// </remarks>
public sealed class MessageAttachmentDto
{
    /// <summary>Gets or sets the document's identifier, as the download route takes it.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Gets or sets the file name to save it under.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    /// <summary>Gets or sets the lower-cased file extension, including the leading dot.</summary>
    [JsonPropertyName("extension")]
    public string Extension { get; set; } = null!;

    /// <summary>Gets or sets the content type derived from the extension.</summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = null!;

    /// <summary>Gets or sets the file size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}
