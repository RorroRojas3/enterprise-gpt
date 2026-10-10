using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto;

/// <summary>
/// A short-lived, pre-authorized link to the original file behind a document. The API authorizes the
/// caller and then hands out this link rather than proxying the bytes, so storage serves the download
/// directly.
/// </summary>
/// <remarks>
/// <see cref="DownloadUrl"/> is a bearer credential for its lifetime: anyone holding it can read the
/// file without signing in. Treat it accordingly — do not log it, persist it, or put it anywhere it
/// outlives <see cref="ExpiresAt"/>.
/// </remarks>
public class DocumentDownloadDto
{
    /// <summary>Gets or sets the signed URL the file can be fetched from until <see cref="ExpiresAt"/>.</summary>
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = null!;

    /// <summary>
    /// Gets or sets the original file name as uploaded. The link already carries this as a
    /// <c>Content-Disposition</c> override, so a browser saves the file under this name without the
    /// client having to supply it.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = null!;

    /// <summary>Gets or sets when <see cref="DownloadUrl"/> stops working and a new one must be requested.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }
}
