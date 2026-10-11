using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Documents;

/// <summary>
/// A short-lived, pre-authorized link to a document's original file, so storage serves the download
/// instead of the API proxying the bytes.
/// </summary>
/// <remarks>
/// <see cref="DownloadUrl"/> is a bearer credential until <see cref="ExpiresAt"/>: never log or persist it.
/// </remarks>
public class DocumentDownloadDto
{
    /// <summary>Gets or sets the signed URL the file can be fetched from until <see cref="ExpiresAt"/>.</summary>
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = null!;

    /// <summary>
    /// The original file name; the link already carries it as a <c>Content-Disposition</c> override.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = null!;

    /// <summary>Gets or sets when <see cref="DownloadUrl"/> stops working and a new one must be requested.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }
}
