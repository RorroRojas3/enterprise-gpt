using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents.Extraction.Models;

namespace Enterprise.Gpt.Service.Documents.Extraction.Interfaces;

/// <summary>
/// A text extractor that also surfaces the grid behind the text it produced.
/// </summary>
/// <remarks>
/// A separate method because only spreadsheets have a grid; one parse yields both halves.
/// </remarks>
public interface ISheetStructureExtractor : IDocumentTextExtractor
{
    /// <summary>
    /// Reads the file into text segments and the per-sheet columns and rows behind them.
    /// </summary>
    /// <returns>Segments in reading order, and one entry per sheet that yielded rows.</returns>
    Task<SheetExtractionResult> ExtractSheetsAsync(
        FileDto file,
        IProgress<DocumentExtractionProgress>? progress,
        CancellationToken cancellationToken);
}
