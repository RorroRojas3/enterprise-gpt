using Enterprise.Gpt.Dto.Documents;

namespace Enterprise.Gpt.Service.Documents.Extraction.Models;

/// <summary>
/// The segments a sheet is retrieved through and the structured grid behind them.
/// </summary>
/// <param name="Segments">The schema card first, then the row windows.</param>
/// <param name="Structure">The persisted columns and cell rows a later query reads.</param>
/// <param name="CellCharacters">Serialized cell length, charged against the upload's budget.</param>
internal sealed record SheetAssembly(
    IReadOnlyList<DocumentSegmentDto> Segments,
    SheetStructureDto Structure,
    int CellCharacters);
