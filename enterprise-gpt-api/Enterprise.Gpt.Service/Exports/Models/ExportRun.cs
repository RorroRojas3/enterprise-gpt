using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// One contiguous span of text within a block, with the styling that applies to all of it.
/// </summary>
public sealed record ExportRun(string Text, ExportRunStyles Styles = ExportRunStyles.None, string? LinkUrl = null);
