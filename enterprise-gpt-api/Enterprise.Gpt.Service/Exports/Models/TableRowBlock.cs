using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>One row of a table.</summary>
/// <param name="Cells">The row's cells, each a sequence of runs.</param>
public sealed record TableRowBlock(IReadOnlyList<IReadOnlyList<ExportRun>> Cells);
