namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// A block-level element of a rendered message.
/// </summary>
/// <remarks>
/// The renderer-neutral middle of the export pipeline: <see cref="MarkdownBlockMapper"/> produces it
/// from Markdig's syntax tree and each binary renderer consumes it, so Word and PDF exports of the
/// same conversation cannot disagree about what the markdown meant. Deliberately smaller than
/// markdown — there is no image arm, because embedding an image means fetching a URL that model
/// output chose, from the API's own network position.
/// </remarks>
public abstract record ExportBlock;
