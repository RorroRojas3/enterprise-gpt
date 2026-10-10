using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A heading, at its markdown level.</summary>
/// <param name="Level">The heading level, 1 through 6.</param>
/// <param name="Runs">The heading text.</param>
public sealed record HeadingBlock(int Level, IReadOnlyList<ExportRun> Runs) : ExportBlock;
