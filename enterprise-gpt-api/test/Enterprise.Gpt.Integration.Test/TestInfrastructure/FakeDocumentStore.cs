using System.Collections.Concurrent;
using Enterprise.Gpt.Repository.Blob;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// In-memory <see cref="IDocumentStore"/> so upload tests exercise the real ingestion pipeline
/// without an Azure Storage account or the Azurite emulator.
/// </summary>
public sealed class FakeDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    private readonly ConcurrentDictionary<string, byte> _uploadedPaths = new();
    private readonly ConcurrentDictionary<string, byte> _uploadedKeys = new();

    /// <summary>Blob paths uploaded since the last reset, without the container.</summary>
    public IReadOnlyCollection<string> UploadedPaths => [.. _uploadedPaths.Keys];

    /// <summary>Uploaded paths prefixed by container, which generated-document tests assert on.</summary>
    public IReadOnlyCollection<string> UploadedKeys => [.. _uploadedKeys.Keys];

    /// <summary>
    /// Whether a blob is still stored; <see cref="UploadedPaths"/> deliberately keeps deleted uploads.
    /// </summary>
    public bool Exists(string container, string blob) => _blobs.ContainsKey($"{container}/{blob}");

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
    /// Echoes the header overrides as the <c>rscd</c>/<c>rsct</c> values a real SAS carries, so a test can
    /// assert the download link renames the file.
    /// </remarks>
    public Uri? GenerateReadUri(string container, string blob, TimeSpan expiresIn,
        string? contentDisposition = null, string? contentType = null)
    {
        List<string> query = ["sig=fake-Read"];

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
