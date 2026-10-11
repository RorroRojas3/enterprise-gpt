namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A heading, at its markdown level.</summary>
public sealed record HeadingBlock(int Level, IReadOnlyList<ExportRun> Runs) : ExportBlock;
