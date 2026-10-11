namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// A pipe table.
/// </summary>
public sealed record TableBlock(TableRowBlock? HeaderRow, IReadOnlyList<TableRowBlock> Rows) : ExportBlock;
