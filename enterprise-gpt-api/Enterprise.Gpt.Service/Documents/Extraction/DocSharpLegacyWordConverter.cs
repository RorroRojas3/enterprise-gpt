using DocSharp.Binary.DocFileFormat;
using DocSharp.Binary.OpenXmlLib;
using DocSharp.Binary.OpenXmlLib.WordprocessingML;
using DocSharp.Binary.StructuredStorage.Reader;
using DocSharp.Binary.WordprocessingMLMapping;
using Enterprise.Gpt.Dto.Documents;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents.Extraction;

/// <summary>
/// Converts legacy binary Word documents (<c>.doc</c>, Word 97–2003) to Open XML (<c>.docx</c>).
/// </summary>
/// <remarks>
/// Document Intelligence does not accept binary <c>.doc</c>; converting keeps it on the same OCR path as
/// every other Word document.
/// </remarks>
public interface ILegacyWordConverter
{
    /// <summary>
    /// Converts the bytes of a <c>.doc</c> file to the bytes of an equivalent <c>.docx</c> file.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="FluentValidation.ValidationException">
    /// The document is malformed, password-protected, or otherwise cannot be converted.
    /// </exception>
    Task<byte[]> ConvertToDocxAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ILegacyWordConverter"/> backed by DocSharp, a pure-managed b2xtranslator fork.
/// </summary>
/// <remarks>
/// Conversion happens entirely in memory, so an uploaded document is never written to the host's disk.
/// </remarks>
public sealed partial class DocSharpLegacyWordConverter(ILogger<DocSharpLegacyWordConverter> logger) : ILegacyWordConverter
{
    private readonly ILogger<DocSharpLegacyWordConverter> _logger = logger;

    /// <inheritdoc />
    public Task<byte[]> ConvertToDocxAsync(byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var converted = Convert(content);

            LogDocConverted(_logger, content.Length, converted.Length);

            return Task.FromResult(converted);
        }
        catch (Exception ex)
        {
            // Deliberately broad: the reader throws a wide, undocumented range of types for malformed
            // input, and none of them is a server fault.
            LogDocConversionFailed(_logger, ex);
            throw new ValidationException(
            [
                new ValidationFailure(nameof(FileDto.FileName),
                    "The document could not be read. It may be corrupt or password-protected — try saving it as .docx and uploading again.")
            ]);
        }
    }

    #region Private methods

    // Synchronous: CPU-bound with no async API, and it runs on a background job thread.
    private static byte[] Convert(byte[] content)
    {
        using var input = new MemoryStream(content, writable: false);
        using var reader = new StructuredStorageReader(input);
        using var output = new MemoryStream();

        var document = new WordDocument(reader);

        // Scoped so the package is flushed into `output` before the bytes are read back.
        using (var docx = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document))
        {
            Converter.Convert(document, docx);
        }

        return output.ToArray();
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Converted a {InputSize} byte .doc document to a {OutputSize} byte .docx document")]
    private static partial void LogDocConverted(ILogger logger, int inputSize, int outputSize);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to convert a legacy .doc document to .docx")]
    private static partial void LogDocConversionFailed(ILogger logger, Exception exception);

    #endregion
}
