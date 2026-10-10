using System.Collections.Frozen;
using Enterprise.Gpt.Service.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service;

/// <summary>
/// The two facts every document write and every download link need: which container a blob lives
/// in, and what content type its extension is served under.
/// </summary>
/// <remarks>
/// Shared by the upload pipeline and the generated-file write path so the two cannot disagree about
/// either — a generated file signed against the uploads container would 404, and a content type
/// resolved twice would drift.
/// </remarks>
internal static class DocumentStorage
{
    /// <summary>The setting naming the container uploaded documents live in.</summary>
    public const string DocumentsContainerKey = "AzureStorage:DocumentsContainer";

    /// <summary>The setting naming the container generated documents live in.</summary>
    public const string GeneratedContainerKey = "AzureStorage:GeneratedContainer";

    /// <summary>Reads a container name out of configuration.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="logger">The caller's logger, so the failure is attributed to it.</param>
    /// <param name="key">One of the two container settings above.</param>
    /// <returns>The configured container name.</returns>
    /// <remarks>
    /// Read on each use rather than cached at construction, so a reloaded configuration takes effect
    /// without a restart.
    /// </remarks>
    /// <exception cref="StorageNotConfiguredException">The container name is not configured.</exception>
    public static string RequireContainer(IConfiguration configuration, ILogger logger, string key)
    {
        var container = configuration.GetValue<string>(key);

        if (string.IsNullOrWhiteSpace(container))
        {
            // Diagnosable as the operator condition it is. Left to the Azure SDK, a missing
            // container name surfaces as an opaque 500 with the message suppressed.
            logger.LogError("{SettingKey} is not configured, so no document blob can be read or written", key);
            throw new StorageNotConfiguredException();
        }

        return container;
    }

    /// <summary>
    /// Maps a stored file extension to the content type the download link is served under.
    /// </summary>
    /// <param name="extension">The lower-cased extension, including the leading dot.</param>
    /// <returns>A content type, or <c>application/octet-stream</c> for anything unrecognised.</returns>
    /// <remarks>
    /// Derived from the extension rather than from a declared media type, because that is whatever
    /// the client — or the model — said it was and nothing validates it, and this value is signed
    /// into the link as the header storage will actually return. Serving a caller's chosen media type
    /// back to them would put the burden of preventing stored XSS entirely on the <c>attachment</c>
    /// disposition. Falling back to a byte stream is safe: every link is an attachment, so the type
    /// only decides which application opens the saved file.
    /// </remarks>
    public static string ResolveContentType(string extension) =>
        _contentTypes.GetValueOrDefault(extension, "application/octet-stream");

    private static readonly FrozenDictionary<string, string> _contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".csv"] = "text/csv",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".md"] = "text/markdown",
        [".pdf"] = "application/pdf",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".txt"] = "text/plain",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
