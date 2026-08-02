using DocSharp.Binary.DocFileFormat;
using DocSharp.Binary.OpenXmlLib;
using DocSharp.Binary.OpenXmlLib.WordprocessingML;
using DocSharp.Binary.StructuredStorage.Reader;
using DocSharp.Binary.WordprocessingMLMapping;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;

namespace Enterprise.Gpt.Service.Converters
{
    /// <summary>
    /// <see cref="ILegacyWordConverter"/> backed by DocSharp, a maintained fork of the b2xtranslator
    /// project, which converts the binary Office 97–2003 formats to Open XML in pure managed code.
    /// </summary>
    /// <remarks>
    /// Conversion happens entirely in memory, so an uploaded document is never written to the host's disk.
    /// </remarks>
    public sealed class DocSharpLegacyWordConverter(ILogger<DocSharpLegacyWordConverter> logger) : ILegacyWordConverter
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

                _logger.LogInformation("Converted a {InputSize} byte .doc document to a {OutputSize} byte .docx document",
                    content.Length, converted.Length);

                return Task.FromResult(converted);
            }
            catch (Exception ex)
            {
                // The underlying reader surfaces malformed input as a wide and undocumented range of
                // exception types, so this catch is deliberately broad: every one of them means the same
                // thing to the caller, and none of them is a server fault.
                _logger.LogWarning(ex, "Failed to convert a legacy .doc document to .docx");
                throw new ValidationException(
                [
                    new ValidationFailure(nameof(FileDto.FileName),
                        "The document could not be read. It may be corrupt or password-protected — try saving it as .docx and uploading again.")
                ]);
            }
        }

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
    }
}
