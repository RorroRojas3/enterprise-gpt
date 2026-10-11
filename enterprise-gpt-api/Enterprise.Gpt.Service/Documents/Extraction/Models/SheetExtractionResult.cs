using Enterprise.Gpt.Dto.Documents;

namespace Enterprise.Gpt.Service.Documents.Extraction.Models;

/// <summary>
/// The text segments and the structured cell data a spreadsheet yielded from one parse.
/// </summary>
public sealed record SheetExtractionResult(
    IReadOnlyList<DocumentSegmentDto> Segments,
    IReadOnlyList<SheetStructureDto> Sheets);
