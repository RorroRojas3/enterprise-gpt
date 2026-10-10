namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A paragraph of running text.</summary>
/// <param name="Runs">The paragraph text.</param>
public sealed record ParagraphBlock(IReadOnlyList<ExportRun> Runs) : ExportBlock;
