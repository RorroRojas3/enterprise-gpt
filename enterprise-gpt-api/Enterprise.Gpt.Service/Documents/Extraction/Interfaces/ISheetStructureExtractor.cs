using Enterprise.Gpt.Dto;

namespace Enterprise.Gpt.Service.Extraction;

/// <summary>
/// The text segments and the structured cell data a spreadsheet yielded from one parse.
/// </summary>
public sealed record SheetExtractionResult(
    IReadOnlyList<DocumentSegmentDto> Segments,
    IReadOnlyList<SheetStructureDto> Sheets);

/// <summary>
/// A text extractor that also surfaces the grid behind the text it produced.
/// </summary>
/// <remarks>
/// A second method rather than a wider <see cref="IDocumentTextExtractor.ExtractAsync"/>: only the
/// spreadsheet formats have a grid to report, and widening the shared contract would make every other
/// extractor answer a question it has no answer to. One parse yields both halves, because opening the
/// package twice would double what an upload costs for nothing.
/// </remarks>
public interface ISheetStructureExtractor : IDocumentTextExtractor
{
    /// <summary>
    /// Reads the file into text segments and the per-sheet columns and rows behind them.
    /// </summary>
    /// <param name="file">The uploaded file.</param>
    /// <param name="progress">Receives extraction progress, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to observe while reading a large workbook.</param>
    /// <returns>The segments in reading order and one entry per sheet that yielded rows.</returns>
    Task<SheetExtractionResult> ExtractSheetsAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken);
}
