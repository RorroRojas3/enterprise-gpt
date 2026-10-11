using System.Collections.Frozen;
using Enterprise.Gpt.Repository.Blob.Options;
using Enterprise.Gpt.Service.Documents.Exceptions;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents;

/// <summary>
/// The two facts every document write and every download link need: which container a blob lives
/// in, and what content type its extension is served under.
/// </summary>
/// <remarks>
/// Shared by the upload pipeline and the generated-file write path so the two cannot disagree about
/// either — a generated file signed against the uploads container would 404, and a content type
/// resolved twice would drift.
/// </remarks>
internal static partial class DocumentStorage
{
    /// <summary>The setting naming the container uploaded documents live in.</summary>
    public const string DocumentsContainerKey = "AzureStorage:DocumentsContainer";

    /// <summary>The setting naming the container generated documents live in.</summary>
    public const string GeneratedContainerKey = "AzureStorage:GeneratedContainer";

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

    #region Private methods

    private static string RequireContainer(string? container, string key, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            // Diagnosable as the operator condition it is. Left to the Azure SDK, a missing
            // container name surfaces as an opaque 500 with the message suppressed.
            LogStorageNotConfigured(logger, key);
            throw new StorageNotConfiguredException();
        }

        return container;
    }

    #endregion

    #region Public static methods

    /// <summary>Returns the container uploaded documents live in.</summary>
    /// <param name="options">The current storage options; pass <c>IOptionsMonitor.CurrentValue</c> so a reload applies without a restart.</param>
    /// <param name="logger">The caller's logger, so the failure is attributed to it.</param>
    /// <exception cref="StorageNotConfiguredException">The container name is not configured.</exception>
    public static string RequireDocumentsContainer(BlobStorageOptions options, ILogger logger) =>
        RequireContainer(options.DocumentsContainer, DocumentsContainerKey, logger);

    /// <summary>Returns the container generated documents live in.</summary>
    /// <param name="options">The current storage options.</param>
    /// <param name="logger">The caller's logger, so the failure is attributed to it.</param>
    /// <exception cref="StorageNotConfiguredException">The container name is not configured.</exception>
    public static string RequireGeneratedContainer(BlobStorageOptions options, ILogger logger) =>
        RequireContainer(options.GeneratedContainer, GeneratedContainerKey, logger);

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

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "{SettingKey} is not configured, so no document blob can be read or written")]
    private static partial void LogStorageNotConfigured(ILogger logger, string settingKey);

    #endregion
}
