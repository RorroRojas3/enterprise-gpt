using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Repository.Blob;

/// <summary>
/// Reads, writes and signs document files in blob storage.
/// </summary>
public interface IDocumentStore
{
    /// <summary>
    /// Downloads the specified blob from the given container.
    /// </summary>
    /// <param name="container">The name of the blob container.</param>
    /// <param name="blob">The name of the blob to download.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A byte array containing the blob's content.</returns>
    Task<byte[]> DownloadAsync(string container, string blob, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a blob to the specified container with optional metadata.
    /// </summary>
    /// <param name="container">The name of the blob container.</param>
    /// <param name="blob">The name of the blob to upload.</param>
    /// <param name="data">The binary content to upload.</param>
    /// <param name="metadata">A collection of user-defined metadata to associate with the blob.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    Task UploadAsync(string container, string blob, byte[] data, Dictionary<string, string> metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the specified blob from the given container if it exists.
    /// </summary>
    /// <param name="container">The name of the blob container.</param>
    /// <param name="blob">The name of the blob to delete.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    /// True if the blob was found and deleted; false if the blob did not exist.
    /// </returns>
    Task<bool> DeleteAsync(string container, string blob, CancellationToken cancellationToken);

    /// <summary>
    /// Generates a read-only signed URI for the specified blob.
    /// </summary>
    /// <param name="container">The name of the blob container.</param>
    /// <param name="blob">The name of the blob to sign.</param>
    /// <param name="expiresIn">The duration after which the signature expires.</param>
    /// <param name="contentDisposition">
    /// Optional <c>Content-Disposition</c> the service returns instead of the blob's own, signed
    /// into the token as <c>rscd</c>.
    /// </param>
    /// <param name="contentType">
    /// Optional <c>Content-Type</c> the service returns instead of the blob's own, signed into the
    /// token as <c>rsct</c>.
    /// </param>
    /// <returns>The signed URI, or <see langword="null"/> when the client holds no credential that can sign.</returns>
    /// <remarks>
    /// The response-header overrides exist because <see cref="UploadAsync"/> stores metadata but no
    /// <c>BlobHttpHeaders</c>: without them a browser saves the file under its storage key and as
    /// <c>application/octet-stream</c>.
    /// </remarks>
    Uri? GenerateReadUri(string container, string blob, TimeSpan expiresIn, string? contentDisposition = null, string? contentType = null);
}

/// <inheritdoc />
public sealed partial class BlobDocumentStore(
    ILogger<BlobDocumentStore> logger, BlobServiceClient blobServiceClient, TimeProvider timeProvider) : IDocumentStore
{
    private readonly ILogger<BlobDocumentStore> _logger = logger;
    private readonly BlobServiceClient _blobServiceClient = blobServiceClient;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<byte[]> DownloadAsync(string container, string blob, CancellationToken cancellationToken)
    {
        LogDownloading(_logger, blob, container);

        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blob);
        var downloadInfo = await blobClient.DownloadAsync(cancellationToken);

        LogDownloaded(_logger, blob, container);

        using var memoryStream = new MemoryStream();
        await downloadInfo.Value.Content.CopyToAsync(memoryStream, cancellationToken);

        return memoryStream.ToArray();
    }

    /// <inheritdoc />
    public async Task UploadAsync(string container, string blob, byte[] data, Dictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        LogUploading(_logger, blob, container);

        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blob);
        using var memoryStream = new MemoryStream(data);

        var options = new BlobUploadOptions
        {
            Metadata = metadata
        };
        await blobClient.UploadAsync(memoryStream, options, cancellationToken);

        LogUploaded(_logger, blob, container);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string container, string blob, CancellationToken cancellationToken)
    {
        LogDeleting(_logger, blob, container);

        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blob);
        var response = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
        if (response.Value)
        {
            LogDeleted(_logger, blob, container);
        }
        else
        {
            LogDeleteMissing(_logger, blob, container);
        }

        return response.Value;
    }

    /// <inheritdoc />
    public Uri? GenerateReadUri(string container, string blob, TimeSpan expiresIn, string? contentDisposition = null, string? contentType = null)
    {
        LogSigning(_logger, blob, container, expiresIn);

        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blob);

        if (!blobClient.CanGenerateSasUri)
        {
            LogCannotSign(_logger);

            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = container,
            BlobName = blob,
            Resource = "b",
            StartsOn = now.AddMinutes(-5),
            ExpiresOn = now.Add(expiresIn)
        };

        // Left unset rather than assigned null: BlobSasBuilder signs every non-null field, so an
        // empty override would be signed as an empty header and strip the real one.
        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            sasBuilder.ContentDisposition = contentDisposition;
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            sasBuilder.ContentType = contentType;
        }

        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var sasUri = blobClient.GenerateSasUri(sasBuilder);

        // The URI itself is deliberately not logged: it grants read access to anyone holding it until it expires.
        LogSigned(_logger, blob, container);

        return sasUri;
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading blob '{Blob}' from container '{Container}'")]
    private static partial void LogDownloading(ILogger logger, string blob, string container);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blob '{Blob}' downloaded successfully from container '{Container}'")]
    private static partial void LogDownloaded(ILogger logger, string blob, string container);

    [LoggerMessage(Level = LogLevel.Information, Message = "Uploading blob '{Blob}' to container '{Container}'")]
    private static partial void LogUploading(ILogger logger, string blob, string container);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blob '{Blob}' uploaded successfully to container '{Container}'")]
    private static partial void LogUploaded(ILogger logger, string blob, string container);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleting blob '{Blob}' from container '{Container}'")]
    private static partial void LogDeleting(ILogger logger, string blob, string container);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blob '{Blob}' deleted successfully from container '{Container}'")]
    private static partial void LogDeleted(ILogger logger, string blob, string container);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Blob '{Blob}' not found in container '{Container}'")]
    private static partial void LogDeleteMissing(ILogger logger, string blob, string container);

    [LoggerMessage(Level = LogLevel.Information, Message = "Generating SAS URI for blob '{Blob}' in container '{Container}' with permissions 'Read' expiring in {ExpiresIn}")]
    private static partial void LogSigning(ILogger logger, string blob, string container, TimeSpan expiresIn);

    [LoggerMessage(Level = LogLevel.Error, Message = "BlobClient cannot generate SAS URI. Ensure the BlobServiceClient is configured with credentials that support SAS generation.")]
    private static partial void LogCannotSign(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "SAS URI generated successfully for blob '{Blob}' in container '{Container}'")]
    private static partial void LogSigned(ILogger logger, string blob, string container);

    #endregion
}
