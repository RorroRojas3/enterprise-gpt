using Enterprise.Gpt.Dto.Documents;

namespace Enterprise.Gpt.Service.Documents.Extraction.Models;

/// <summary>
/// The segments a sheet is retrieved through and the structured grid behind them.
/// </summary>
/// <param name="Segments">The schema card first, then the header-repeating row windows, each numbered with the sheet's index.</param>
/// <param name="Structure">The columns and cell rows persisted behind the segments, which a later query reads.</param>
/// <param name="CellCharacters">Length of the serialized cell data, to charge against the upload's budget.</param>
internal sealed record SheetAssembly(
    IReadOnlyList<DocumentSegmentDto> Segments,
    SheetStructureDto Structure,
    int CellCharacters);
