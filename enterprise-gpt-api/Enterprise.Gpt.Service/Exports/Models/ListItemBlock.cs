namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>One item of a list, which may itself contain paragraphs, code, or nested lists.</summary>
/// <param name="Children">The item's blocks.</param>
public sealed record ListItemBlock(IReadOnlyList<ExportBlock> Children);
