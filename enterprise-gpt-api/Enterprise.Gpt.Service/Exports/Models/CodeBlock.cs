namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A fenced or indented code block.</summary>
/// <param name="Language">The fence's info string, or <see langword="null"/> when it carried none.</param>
/// <param name="Text">The code, with its line breaks intact and no styling of any kind.</param>
public sealed record CodeBlock(string? Language, string Text) : ExportBlock;
