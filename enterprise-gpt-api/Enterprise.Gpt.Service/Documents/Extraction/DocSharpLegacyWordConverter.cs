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
/// Azure Document Intelligence accepts PDF, images and the OOXML Office formats, but not the binary
/// <c>.doc</c> container. Converting first means <c>.doc</c> uploads take exactly the same OCR path as
/// every other Word document instead of needing a second, lower-fidelity extraction route.
/// </remarks>
public interface ILegacyWordConverter
{
    /// <summary>
    /// Converts the bytes of a <c>.doc</c> file to the bytes of an equivalent <c>.docx</c> file.
    /// </summary>
    /// <param name="content">The binary <c>.doc</c> content. Must not be <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The converted <c>.docx</c> content.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="FluentValidation.ValidationException">
    /// Thrown when the document is malformed, password-protected, or otherwise cannot be converted.
    /// </exception>
    Task<byte[]> ConvertToDocxAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ILegacyWordConverter"/> backed by DocSharp, a maintained fork of the b2xtranslator
/// project, which converts the binary Office 97–2003 formats to Open XML in pure managed code.
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
            // The underlying reader surfaces malformed input as a wide and undocumented range of
            // exception types, so this catch is deliberately broad: every one of them means the same
            // thing to the caller, and none of them is a server fault.
            LogDocConversionFailed(_logger, ex);
            throw new ValidationException(
            [
                new ValidationFailure(nameof(FileDto.FileName),
                    "The document could not be read. It may be corrupt or password-protected — try saving it as .docx and uploading again.")
            ]);
        }
    }

    #region Private methods

    // Synchronous because the conversion is CPU-bound and the library exposes no async surface; it runs
    // on a background job thread, never on a request thread.
    private static byte[] Convert(byte[] content)
    {
        using var input = new MemoryStream(content, writable: false);
        using var reader = new StructuredStorageReader(input);
        using var output = new MemoryStream();

        var document = new WordDocument(reader);

        // Scoped so the package is disposed — and therefore flushed into `output` — before the bytes
        // are read back out.
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
