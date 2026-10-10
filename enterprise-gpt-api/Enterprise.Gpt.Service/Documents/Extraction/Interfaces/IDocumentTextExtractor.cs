using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;

namespace Enterprise.Gpt.Service.Extraction
{
    /// <summary>
    /// Best-effort progress report emitted while a document is being read.
    /// </summary>
    /// <param name="Fraction">
    /// How far through the extraction the reader is, in the range 0..1, or <see langword="null"/> when the
    /// underlying operation cannot be measured (for example a remote OCR call, which is opaque until it
    /// completes). A <see langword="null"/> fraction leaves the reported percentage where it is and
    /// refreshes only <paramref name="Message"/>, so the percentage never claims progress it cannot know.
    /// </param>
    /// <param name="Message">A short, user-facing description of what is happening right now.</param>
    public sealed record DocumentExtractionProgress(double? Fraction, string Message);

    /// <summary>
    /// Reads the text out of one family of document formats.
    /// </summary>
    /// <remarks>
    /// Implementations are registered in DI as <see cref="IDocumentTextExtractor"/> and selected by
    /// <see cref="IDocumentTextExtractorFactory"/>. Registering a new implementation is all that is needed
    /// to support a new format — the upload validator and the <c>file-extensions</c> endpoint both derive
    /// the accepted set from the registrations.
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
        /// <param name="file">The uploaded file. Must not be <see langword="null"/>.</param>
        /// <param name="progress">Optional sink for progress reports; may be <see langword="null"/>.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>
        /// The extracted segments in reading order. Segments that are empty or whitespace are omitted, so
        /// the result may be empty for a document with no readable text.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is <see langword="null"/>.</exception>
        /// <exception cref="FluentValidation.ValidationException">
        /// Thrown when the file is malformed, password-protected, or otherwise unreadable — conditions the
        /// caller can act on, so they surface as a 400 rather than a 500.
        /// </exception>
        Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
            FileDto file,
            IProgress<DocumentExtractionProgress>? progress,
            CancellationToken cancellationToken);
    }
}
