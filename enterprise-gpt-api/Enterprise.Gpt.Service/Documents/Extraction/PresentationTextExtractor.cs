using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Extraction.Interfaces;
using Enterprise.Gpt.Service.Documents.Extraction.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using A = DocumentFormat.OpenXml.Drawing;

namespace Enterprise.Gpt.Service.Documents.Extraction;

/// <summary>
/// Reads PowerPoint (<c>.pptx</c>) presentations locally with the Open XML SDK, one segment per slide.
/// </summary>
/// <remarks>
/// Local parsing is free and lossless here, and unlike OCR yields real slide numbers and speaker notes.
/// </remarks>
public sealed partial class PresentationTextExtractor(ILogger<PresentationTextExtractor> logger) : IDocumentTextExtractor
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

        // The whole parse, not just Open(): the SDK reads parts lazily, so malformed slides throw later and
        // would otherwise fail the job opaquely instead of as a 400.
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
            LogPresentationReadFailed(_logger, ex);
            throw new ValidationException(
            [
                new ValidationFailure(nameof(FileDto.FileName),
                    "The presentation could not be read. It may be corrupt or password-protected.")
            ]);
        }
    }

    #region Private methods

    private Task<IReadOnlyList<DocumentSegmentDto>> ExtractSlides(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(file.Content, writable: false);

        using (var document = PresentationDocument.Open(stream, isEditable: false))
        {
            var presentationPart = document.PresentationPart;
            // SlideIdList, not SlideParts: only it is in presentation order.
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

            LogSlidesExtracted(_logger, segments.Count, slideIds.Count);

            return Task.FromResult<IReadOnlyList<DocumentSegmentDto>>(segments);
        }
    }

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

            // Skip the slide-number placeholder; its text is noise.
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

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read a PowerPoint presentation")]
    private static partial void LogPresentationReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted text from {SegmentCount} of {SlideCount} slide(s)")]
    private static partial void LogSlidesExtracted(ILogger logger, int segmentCount, int slideCount);

    #endregion
}
