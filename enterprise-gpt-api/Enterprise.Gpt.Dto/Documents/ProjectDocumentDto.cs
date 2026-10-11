using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto.Documents;

/// <summary>A project document as listed; carries no chunk or embedding data.</summary>
public class ProjectDocumentDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("projectId")]
    public Guid ProjectId { get; set; }

    /// <summary>Alias of <see cref="Id"/> so clients key this and upload-status responses alike.</summary>
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
