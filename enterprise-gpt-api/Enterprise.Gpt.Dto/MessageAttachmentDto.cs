using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto;

/// <summary>
/// The identity of a file introduced by an assistant message, as carried on the transcript and
/// returned to a client.
/// </summary>
/// <remarks>
/// An identity and never a credential: a download link is minted on click through the document
/// download route, so nothing here is usable on its own.
/// <para>
/// The <see cref="JsonPropertyNameAttribute"/>s are load-bearing beyond the HTTP wire: this type is
/// also serialized with default options onto the streamed turn's metadata, where they are the only
/// thing making the payload camelCase for the client that reads it.
/// </para>
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
