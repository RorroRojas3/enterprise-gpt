using Azure;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Converters;

namespace Enterprise.Gpt.Service.Extraction
{
    /// <summary>
    /// Reads PDF and Word documents with Azure AI Document Intelligence, so scanned and image-only files
    /// are OCR'd rather than yielding nothing.
    /// </summary>
    /// <remarks>
    /// Document Intelligence accepts <c>.pdf</c> and <c>.docx</c> directly. The binary <c>.doc</c> format is
    /// not an accepted input, so it is converted to <c>.docx</c> first and then follows the same path —
    /// which keeps a single OCR-backed route for every Word document rather than a second, weaker one.
    /// </remarks>
    public sealed class DocumentIntelligenceTextExtractor(
        ILogger<DocumentIntelligenceTextExtractor> logger,
        IDocumentIntelligenceService documentIntelligenceService,
        ILegacyWordConverter legacyWordConverter) : IDocumentTextExtractor
    {
        private readonly ILogger<DocumentIntelligenceTextExtractor> _logger = logger;
        private readonly IDocumentIntelligenceService _documentIntelligenceService = documentIntelligenceService;
        private readonly ILegacyWordConverter _legacyWordConverter = legacyWordConverter;

        /// <inheritdoc />
        public IReadOnlyCollection<FileExtensions> SupportedExtensions { get; } =
        [
            FileExtensions.Pdf,
            FileExtensions.Doc,
            FileExtensions.Docx
        ];

        /// <inheritdoc />
        public async Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
            FileDto file,
            IProgress<DocumentExtractionProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(file);

            var content = file.Content;

            if (file.FileExtension == FileExtensions.Doc)
            {
                progress?.Report(new DocumentExtractionProgress(null, "Converting the Word 97-2003 document"));
                content = await _legacyWordConverter.ConvertToDocxAsync(content, cancellationToken);
            }

            progress?.Report(new DocumentExtractionProgress(null, "Sending the document for text recognition"));

            Azure.AI.DocumentIntelligence.AnalyzeResult result;
            try
            {
                result = await _documentIntelligenceService.ReadAsync(
                    content,
                    elapsed => progress?.Report(new DocumentExtractionProgress(
                        null,
                        $"Recognizing text ({FormatElapsed(elapsed)} elapsed)")),
                    cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status is 400 or 415)
            {
                // The service rejected the document itself (corrupt, password-protected, unsupported
                // variant). That is the caller's problem to fix, so surface it as a 400 rather than a 500.
                _logger.LogWarning(ex, "Document Intelligence rejected the uploaded document (status {Status})", ex.Status);
                throw new ValidationException(
                [
                    new ValidationFailure(nameof(FileDto.FileName),
                        "The document could not be read. It may be corrupt, password-protected, or an unsupported variant of the format.")
                ]);
            }

            var segments = result.Pages
                .Select(page => new DocumentSegmentDto
                {
                    SourceNumber = page.PageNumber,
                    Text = string.Join("\n", page.Lines.Select(line => line.Content))
                })
                .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
                .ToList();

            // Office inputs are not always paginated by the service. Falling back to the flat content keeps
            // a Word document from silently producing zero chunks.
            if (segments.Count == 0 && !string.IsNullOrWhiteSpace(result.Content))
            {
                segments.Add(new DocumentSegmentDto { SourceNumber = null, Text = result.Content });
            }

            progress?.Report(new DocumentExtractionProgress(1d, $"Recognized text from {segments.Count} page(s)"));
            _logger.LogInformation("Extracted {SegmentCount} segment(s) from a {Extension} document via Document Intelligence",
                segments.Count, file.FileExtension);

            return segments;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            return elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : $"{elapsed.Seconds}s";
        }
    }
}
