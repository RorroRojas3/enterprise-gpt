using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Extraction.Models;

namespace Enterprise.Gpt.Service.Documents.Extraction.Interfaces;

/// <summary>
/// Reads the text out of one family of document formats.
/// </summary>
/// <remarks>
/// Registering an implementation is all a new format needs: <see cref="IDocumentTextExtractorFactory"/>,
/// the upload validator and the <c>file-extensions</c> endpoint all derive the accepted set from DI.
/// </remarks>
public interface IDocumentTextExtractor
{
    /// <summary>
    /// The file extensions this extractor can read.
    /// </summary>
    IReadOnlyCollection<FileExtensions> SupportedExtensions { get; }

    /// <summary>
    /// Extracts the text of <paramref name="file"/> as an ordered list of segments.
    /// </summary>
    /// <returns>Non-blank segments in reading order; empty for a document with no readable text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    /// <exception cref="FluentValidation.ValidationException">
    /// The file is malformed, password-protected, or otherwise unreadable (a 400, not a 500).
    /// </exception>
    Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken);
}
