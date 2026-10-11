namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A block quote, which may contain any other block.</summary>
public sealed record QuoteBlock(IReadOnlyList<ExportBlock> Children) : ExportBlock;
