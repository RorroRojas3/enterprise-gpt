namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// The inline styling an <see cref="ExportRun"/> carries.
/// </summary>
/// <remarks>
/// Flags rather than an enum because markdown nests emphasis freely — <c>**bold _and italic_**</c> is
/// one run wearing two styles, not a choice between them.
/// </remarks>
[Flags]
public enum ExportRunStyles
{
    /// <summary>Plain body text.</summary>
    None = 0,

    /// <summary>Strong emphasis.</summary>
    Bold = 1,

    /// <summary>Emphasis.</summary>
    Italic = 2,

    /// <summary>Inline code, rendered in the monospace face.</summary>
    Code = 4
}

/// <summary>
/// One contiguous span of text within a block, with the styling that applies to all of it.
/// </summary>
/// <param name="Text">The literal text. Never null; may be empty only in a run nothing emits.</param>
/// <param name="Styles">The styles that apply.</param>
/// <param name="LinkUrl">The link target, or <see langword="null"/> when the run is not a link.</param>
public sealed record ExportRun(string Text, ExportRunStyles Styles = ExportRunStyles.None, string? LinkUrl = null);

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

/// <summary>A heading, at its markdown level.</summary>
/// <param name="Level">The heading level, 1 through 6.</param>
/// <param name="Runs">The heading text.</param>
public sealed record HeadingBlock(int Level, IReadOnlyList<ExportRun> Runs) : ExportBlock;

/// <summary>A paragraph of running text.</summary>
/// <param name="Runs">The paragraph text.</param>
public sealed record ParagraphBlock(IReadOnlyList<ExportRun> Runs) : ExportBlock;

/// <summary>A fenced or indented code block.</summary>
/// <param name="Language">The fence's info string, or <see langword="null"/> when it carried none.</param>
/// <param name="Text">The code, with its line breaks intact and no styling of any kind.</param>
public sealed record CodeBlock(string? Language, string Text) : ExportBlock;

/// <summary>A block quote, which may contain any other block.</summary>
/// <param name="Children">The quoted blocks.</param>
public sealed record QuoteBlock(IReadOnlyList<ExportBlock> Children) : ExportBlock;

/// <summary>One item of a list, which may itself contain paragraphs, code, or nested lists.</summary>
/// <param name="Children">The item's blocks.</param>
public sealed record ListItemBlock(IReadOnlyList<ExportBlock> Children);

/// <summary>A bulleted or numbered list.</summary>
/// <param name="Ordered"><see langword="true"/> for a numbered list.</param>
/// <param name="Items">The list's items.</param>
public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItemBlock> Items) : ExportBlock;

/// <summary>One row of a table.</summary>
/// <param name="Cells">The row's cells, each a sequence of runs.</param>
public sealed record TableRowBlock(IReadOnlyList<IReadOnlyList<ExportRun>> Cells);

/// <summary>
/// A pipe table.
/// </summary>
/// <param name="HeaderRow">The header row, or <see langword="null"/> when the table declared none.</param>
/// <param name="Rows">The body rows.</param>
public sealed record TableBlock(TableRowBlock? HeaderRow, IReadOnlyList<TableRowBlock> Rows) : ExportBlock;

/// <summary>A horizontal rule.</summary>
public sealed record ThematicBreakBlock : ExportBlock;
