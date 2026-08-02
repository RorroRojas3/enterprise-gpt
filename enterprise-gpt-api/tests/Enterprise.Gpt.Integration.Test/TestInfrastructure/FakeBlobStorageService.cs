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

    /// <summary>
    /// Gets the blob paths (excluding the container) uploaded since the last reset.
    /// </summary>
    public IReadOnlyCollection<string> UploadedPaths => [.. _uploadedPaths.Keys];

    /// <summary>
    /// Clears all stored blobs.
    /// </summary>
    public void Reset()
    {
        _blobs.Clear();
        _uploadedPaths.Clear();
    }

    /// <inheritdoc />
    public Task UploadAsync(string container, string blob, byte[] data, Dictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        _blobs[$"{container}/{blob}"] = data;
        _uploadedPaths[blob] = 0;
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
    public Uri GenerateSasUri(string container, string blob, TimeSpan expiresIn, BlobSasPermissions permissions)
    {
        return new Uri($"https://localhost/{container}/{blob}");
    }
}
