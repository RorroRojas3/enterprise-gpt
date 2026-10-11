namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// A block-level element of a rendered message.
/// </summary>
/// <remarks>
/// The renderer-neutral model <see cref="MarkdownBlockMapper"/> produces, so Word and PDF cannot disagree
/// about the markdown. It has no image arm; see <see cref="MarkdownBlockMapper"/>.
/// </remarks>
public abstract record ExportBlock;
