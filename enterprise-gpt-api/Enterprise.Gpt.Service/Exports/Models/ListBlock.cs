using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A bulleted or numbered list.</summary>
/// <param name="Ordered"><see langword="true"/> for a numbered list.</param>
/// <param name="Items">The list's items.</param>
public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItemBlock> Items) : ExportBlock;
