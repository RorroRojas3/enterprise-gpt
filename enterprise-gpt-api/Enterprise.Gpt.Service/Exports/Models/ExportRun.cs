using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// One contiguous span of text within a block, with the styling that applies to all of it.
/// </summary>
/// <param name="Text">The literal text. Never null; may be empty only in a run nothing emits.</param>
/// <param name="Styles">The styles that apply.</param>
/// <param name="LinkUrl">The link target, or <see langword="null"/> when the run is not a link.</param>
public sealed record ExportRun(string Text, ExportRunStyles Styles = ExportRunStyles.None, string? LinkUrl = null);
