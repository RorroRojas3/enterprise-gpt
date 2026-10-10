using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;

namespace Enterprise.Gpt.Service.Documents.Extraction.Models;

/// <summary>
/// The segments a sheet is retrieved through and the structured grid behind them.
/// </summary>
/// <param name="CellCharacters">Length of the serialized cell data, to charge against the upload's budget.</param>
internal sealed record SheetAssembly(
    IReadOnlyList<DocumentSegmentDto> Segments,
    SheetStructureDto Structure,
    int CellCharacters);
