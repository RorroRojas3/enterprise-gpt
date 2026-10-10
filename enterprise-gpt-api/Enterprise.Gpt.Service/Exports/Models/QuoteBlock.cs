namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A block quote, which may contain any other block.</summary>
/// <param name="Children">The quoted blocks.</param>
public sealed record QuoteBlock(IReadOnlyList<ExportBlock> Children) : ExportBlock;
