using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// A pipe table.
/// </summary>
/// <param name="HeaderRow">The header row, or <see langword="null"/> when the table declared none.</param>
/// <param name="Rows">The body rows.</param>
public sealed record TableBlock(TableRowBlock? HeaderRow, IReadOnlyList<TableRowBlock> Rows) : ExportBlock;
