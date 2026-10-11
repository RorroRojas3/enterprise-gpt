namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>One row of a table.</summary>
public sealed record TableRowBlock(IReadOnlyList<IReadOnlyList<ExportRun>> Cells);
