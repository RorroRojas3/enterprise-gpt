using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using System.Text;
using System.Xml;
using A = DocumentFormat.OpenXml.Drawing;

namespace Enterprise.Gpt.Service.Extraction
{
    /// <summary>
    /// Reads PowerPoint (<c>.pptx</c>) presentations locally with the Open XML SDK, one segment per slide.
    /// </summary>
    /// <remarks>
    /// Parsing locally rather than through OCR is both free and lossless for these files: a <c>.pptx</c>
    /// already stores its text as text. It also yields a real slide number for provenance, and lets speaker
    /// notes be picked up, neither of which a rasterizing OCR pass would give.
    /// </remarks>
    public sealed class PresentationTextExtractor(ILogger<PresentationTextExtractor> logger) : IDocumentTextExtractor
    {
        private readonly ILogger<PresentationTextExtractor> _logger = logger;

        /// <inheritdoc />
        public IReadOnlyCollection<FileExtensions> SupportedExtensions { get; } = [FileExtensions.Pptx];

        /// <inheritdoc />
        public Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
            FileDto file,
            IProgress<DocumentExtractionProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(file);

            // The whole parse is guarded, not just Open(). The Open XML SDK reads part XML lazily, so a
            // truncated slide, malformed markup, or a slide id pointing at a missing part all throw well
            // after the package opens — and unguarded they would surface as an opaque "unexpected error"
            // job failure instead of a 400 the caller can act on.
            try
            {
                return ExtractSlides(file, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is OpenXmlPackageException
                or FileFormatException
                or InvalidDataException
                or XmlException
                or ArgumentOutOfRangeException
                or KeyNotFoundException)
            {
                _logger.LogWarning(ex, "Failed to read a PowerPoint presentation");
                throw new ValidationException(
                [
                    new ValidationFailure(nameof(FileDto.FileName),
                        "The presentation could not be read. It may be corrupt or password-protected.")
                ]);
            }
        }

        private Task<IReadOnlyList<DocumentSegmentDto>> ExtractSlides(
            FileDto file,
            IProgress<DocumentExtractionProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream(file.Content, writable: false);

            using (var document = PresentationDocument.Open(stream, isEditable: false))
            {
                var presentationPart = document.PresentationPart;
                var slideIds = presentationPart?.Presentation?.SlideIdList?.ChildElements
                    .OfType<SlideId>()
                    .ToList() ?? [];

                var segments = new List<DocumentSegmentDto>(slideIds.Count);

                for (var i = 0; i < slideIds.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relationshipId = slideIds[i].RelationshipId?.Value;
                    if (relationshipId is null || presentationPart!.GetPartById(relationshipId) is not SlidePart slidePart)
                    {
                        continue;
                    }

                    var slideNumber = i + 1;
                    var text = BuildSlideText(slidePart);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        segments.Add(new DocumentSegmentDto { SourceNumber = slideNumber, Text = text });
                    }

                    progress?.Report(new DocumentExtractionProgress(
                        (double)slideNumber / slideIds.Count,
                        $"Reading slide {slideNumber} of {slideIds.Count}"));
                }

                _logger.LogInformation("Extracted text from {SegmentCount} of {SlideCount} slide(s)", segments.Count, slideIds.Count);

                return Task.FromResult<IReadOnlyList<DocumentSegmentDto>>(segments);
            }
        }

        // Slide ids are enumerated through SlideIdList rather than PresentationPart.SlideParts because only
        // the former is in presentation order; part order is an implementation detail of the package.
        private static string BuildSlideText(SlidePart slidePart)
        {
            var builder = new StringBuilder();

            if (slidePart.Slide is { } slide)
            {
                AppendParagraphs(builder, slide);
            }

            var notesSlide = slidePart.NotesSlidePart?.NotesSlide;
            if (notesSlide is not null)
            {
                var notes = new StringBuilder();

                // Notes slides carry a slide-number placeholder whose text is just the slide number —
                // noise that would otherwise be embedded as content.
                foreach (var shape in notesSlide.Descendants<Shape>())
                {
                    var placeholderType = shape.NonVisualShapeProperties
                        ?.ApplicationNonVisualDrawingProperties
                        ?.PlaceholderShape
                        ?.Type;

                    if (placeholderType is not null && placeholderType == PlaceholderValues.SlideNumber)
                    {
                        continue;
                    }

                    AppendParagraphs(notes, shape);
                }

                if (notes.Length > 0)
                {
                    builder.Append("Speaker notes:\n").Append(notes);
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static void AppendParagraphs(StringBuilder builder, OpenXmlElement root)
        {
            foreach (var paragraph in root.Descendants<A.Paragraph>())
            {
                var line = string.Concat(paragraph.Descendants<A.Text>().Select(t => t.Text));

                if (!string.IsNullOrWhiteSpace(line))
                {
                    builder.Append(line.Trim()).Append('\n');
                }
            }
        }
    }
}
