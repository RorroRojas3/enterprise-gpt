namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A paragraph of running text.</summary>
public sealed record ParagraphBlock(IReadOnlyList<ExportRun> Runs) : ExportBlock;
