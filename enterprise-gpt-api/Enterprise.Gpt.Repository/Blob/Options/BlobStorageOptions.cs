namespace Enterprise.Gpt.Repository.Blob.Options;

/// <summary>
/// Azure Blob Storage settings for document files, bound from the <c>AzureStorage</c> configuration section.
/// </summary>
/// <remarks>
/// No startup validator: a blank container name surfaces as a 503 <c>storage-not-configured</c> problem
/// when that container is first needed, and a blank connection string fails when the client is first built.
/// </remarks>
public sealed class BlobStorageOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "AzureStorage";

    /// <summary>The storage account connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>The container uploaded documents are stored in.</summary>
    public string? DocumentsContainer { get; set; }

    /// <summary>The container the File Agent's generated documents are stored in.</summary>
    public string? GeneratedContainer { get; set; }
}
