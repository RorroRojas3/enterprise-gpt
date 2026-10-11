using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Extraction.Interfaces;
using Enterprise.Gpt.Service.Documents.Extraction.Models;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents.Extraction;

/// <summary>
/// Reads plain text (<c>.txt</c>) and Markdown (<c>.md</c>) files straight off the byte stream.
/// </summary>
/// <remarks>
/// Markdown is embedded as written rather than being rendered down to prose: the markup carries real
/// structure (headings, lists, code fences) that helps both the embedding and the eventual answer, and
/// stripping it would discard that for no benefit.
/// </remarks>
public sealed partial class PlainTextExtractor(ILogger<PlainTextExtractor> logger) : IDocumentTextExtractor
{
    private readonly ILogger<PlainTextExtractor> _logger = logger;

    /// <inheritdoc />
    public IReadOnlyCollection<FileExtensions> SupportedExtensions { get; } =
    [
        FileExtensions.Md,
        FileExtensions.Txt
    ];

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        progress?.Report(new DocumentExtractionProgress(null, "Reading text file"));

        using var stream = new MemoryStream(file.Content, writable: false);

        // UTF-8 is the fallback rather than the assumption: a byte-order mark for UTF-16/UTF-32 wins,
        // which is what Notepad and most Windows editors emit.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken);

        // Normalize line endings so chunk boundaries and token counts do not depend on the authoring OS.
        text = text.ReplaceLineEndings("\n");

        List<DocumentSegmentDto> segments = string.IsNullOrWhiteSpace(text)
            ? []
            : [new DocumentSegmentDto { SourceNumber = null, Text = text }];

        progress?.Report(new DocumentExtractionProgress(1d, "Read text file"));
        LogCharactersExtracted(_logger, text.Length, file.FileExtension);

        return segments;
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted {CharacterCount} character(s) from a {Extension} file")]
    private static partial void LogCharactersExtracted(ILogger logger, int characterCount, FileExtensions extension);

    #endregion
}
