namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A bulleted or numbered list.</summary>
public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItemBlock> Items) : ExportBlock;
