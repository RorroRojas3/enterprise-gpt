using System.Collections.Frozen;
using Enterprise.Gpt.Repository.Blob.Options;
using Enterprise.Gpt.Service.Documents.Exceptions;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents;

/// <summary>
/// Blob container and content-type resolution, shared by the upload and generated-file paths so the two
/// cannot disagree (a generated file signed against the uploads container would 404).
/// </summary>
internal static partial class DocumentStorage
{
    public const string DocumentsContainerKey = "AzureStorage:DocumentsContainer";

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
            // Left to the Azure SDK, a missing container name surfaces as an opaque 500.
            LogStorageNotConfigured(logger, key);
            throw new StorageNotConfiguredException();
        }

        return container;
    }

    #endregion

    #region Public static methods

    /// <exception cref="StorageNotConfiguredException">The container name is not configured.</exception>
    public static string RequireDocumentsContainer(BlobStorageOptions options, ILogger logger) =>
        RequireContainer(options.DocumentsContainer, DocumentsContainerKey, logger);

    /// <exception cref="StorageNotConfiguredException">The container name is not configured.</exception>
    public static string RequireGeneratedContainer(BlobStorageOptions options, ILogger logger) =>
        RequireContainer(options.GeneratedContainer, GeneratedContainerKey, logger);

    /// <summary>
    /// Maps a stored file extension (with its dot) to the content type the download link is served under.
    /// </summary>
    /// <remarks>
    /// Never the declared media type: that is unvalidated client or model input, and this value is signed
    /// into the link as the served header, so honouring it would leave stored-XSS defence to the disposition.
    /// </remarks>
    public static string ResolveContentType(string extension) =>
        _contentTypes.GetValueOrDefault(extension, "application/octet-stream");

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "{SettingKey} is not configured, so no document blob can be read or written")]
    private static partial void LogStorageNotConfigured(ILogger logger, string settingKey);

    #endregion
}
