namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>A fenced or indented code block.</summary>
public sealed record CodeBlock(string? Language, string Text) : ExportBlock;
