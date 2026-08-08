using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto;

/// <summary>
/// A document uploaded into a project, as returned by the project document listing. Carries no chunk
/// or embedding data.
/// </summary>
public class ProjectDocumentDto
{
    /// <summary>Gets or sets the unique identifier of the document.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Gets or sets the owning project.</summary>
    [JsonPropertyName("projectId")]
    public Guid ProjectId { get; set; }

    /// <summary>
    /// Alias of <see cref="Id"/>, matching the shape the upload-status response uses for conversation
    /// documents so a client can key both listings the same way.
    /// </summary>
    [JsonPropertyName("documentId")]
    public Guid DocumentId => Id;

    /// <summary>Gets or sets the original file name as uploaded.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    /// <summary>Gets or sets the lower-cased file extension, including the leading dot.</summary>
    [JsonPropertyName("extension")]
    public string Extension { get; set; } = null!;

    /// <summary>Gets or sets the content type reported by the client at upload time.</summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = null!;

    /// <summary>Gets or sets the file size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Gets or sets when the document finished ingesting.</summary>
    [JsonPropertyName("dateCreated")]
    public DateTimeOffset DateCreated { get; set; }
}
