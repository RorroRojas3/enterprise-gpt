using System.Collections.Concurrent;
using Azure.Storage.Sas;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// In-memory <see cref="IBlobStorageService"/> so upload tests exercise the real ingestion pipeline
/// without an Azure Storage account or the Azurite emulator.
/// </summary>
public sealed class FakeBlobStorageService : IBlobStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    private readonly ConcurrentDictionary<string, byte> _uploadedPaths = new();
    private readonly ConcurrentDictionary<string, byte> _uploadedKeys = new();

    /// <summary>
    /// Gets the blob paths (excluding the container) uploaded since the last reset.
    /// </summary>
    public IReadOnlyCollection<string> UploadedPaths => [.. _uploadedPaths.Keys];

    /// <summary>
    /// Gets the blob paths uploaded since the last reset, each prefixed by its container.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UploadedPaths"/> because that one drops the container, and which of
    /// the two containers a write landed in is exactly what a generated-document test asserts.
    /// </remarks>
    public IReadOnlyCollection<string> UploadedKeys => [.. _uploadedKeys.Keys];

    /// <summary>
    /// Clears all stored blobs.
    /// </summary>
    public void Reset()
    {
        _blobs.Clear();
        _uploadedPaths.Clear();
        _uploadedKeys.Clear();
    }

    /// <inheritdoc />
    public Task UploadAsync(string container, string blob, byte[] data, Dictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        _blobs[$"{container}/{blob}"] = data;
        _uploadedPaths[blob] = 0;
        _uploadedKeys[$"{container}/{blob}"] = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]> DownloadAsync(string container, string blob, CancellationToken cancellationToken)
    {
        return Task.FromResult(_blobs.TryGetValue($"{container}/{blob}", out var data) ? data : []);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string container, string blob, CancellationToken cancellationToken)
    {
        return Task.FromResult(_blobs.TryRemove($"{container}/{blob}", out _));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The response-header overrides are echoed back as <c>rscd</c> and <c>rsct</c> query values — the
    /// names a real service SAS carries them under — so a test can assert the download link really does
    /// rename the file rather than serving it under its storage key.
    /// </remarks>
    public Uri GenerateSasUri(string container, string blob, TimeSpan expiresIn, BlobSasPermissions permissions,
        string? contentDisposition = null, string? contentType = null)
    {
        List<string> query = [$"sig={Uri.EscapeDataString($"fake-{permissions}")}"];

        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            query.Add($"rscd={Uri.EscapeDataString(contentDisposition)}");
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            query.Add($"rsct={Uri.EscapeDataString(contentType)}");
        }

        return new Uri($"https://localhost/{container}/{blob}?{string.Join('&', query)}");
    }
}
