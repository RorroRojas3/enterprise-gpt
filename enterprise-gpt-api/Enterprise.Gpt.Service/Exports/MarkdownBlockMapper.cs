using System.Text;
using Enterprise.Gpt.Service.Rendering;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Md = Markdig.Syntax;

namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// Turns a message's markdown into the renderer-neutral block model.
/// </summary>
public interface IMarkdownBlockMapper
{
    /// <summary>
    /// Maps markdown into blocks.
    /// </summary>
    /// <param name="markdown">The message text.</param>
    /// <returns>
    /// The blocks, in document order. Empty input maps to an empty list, and text that parses to
    /// nothing renderable does too.
    /// </returns>
    /// <remarks>
    /// Never throws: a message that cannot be parsed still belongs in an export, and degrades to a
    /// single paragraph holding its own text.
    /// </remarks>
    IReadOnlyList<ExportBlock> Map(string? markdown);
}

/// <summary>
/// Walks Markdig's syntax tree into <see cref="ExportBlock"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Parsed through <see cref="MarkdownPipelines.Parse"/>, so the link-scheme allowlist and the raw-HTML
/// refusal that guard the stored HTML guard an exported document too. That matters more here than it
/// looks: a <c>javascript:</c> hyperlink is live in Word and in most PDF readers.
/// </para>
/// <para>
/// The mapping is deliberately lossy in one place. <strong>Images are dropped and their alt text kept
/// as ordinary runs</strong> — embedding one means the API issuing an outbound request to a URL that
/// model output chose, from inside the network position the API holds, which is a server-side request
/// forgery surface that a transcript export has no reason to open.
/// </para>
/// </remarks>
public sealed class MarkdownBlockMapper : IMarkdownBlockMapper
{
    /// <summary>
    /// How deep a nested structure is followed before the walk stops descending.
    /// </summary>
    /// <remarks>
    /// A fidelity limit: past it a container keeps its own text and loses its structure, because
    /// sixteen levels of nesting is already past what any of these formats renders legibly. The stack
    /// is bounded separately, by <see cref="MaxFlattenDepth"/> — and, outside this class, by Markdig
    /// itself, which refuses input nested deeper than 128 levels (measured against the pinned 1.3.2)
    /// and whose refusal is what <see cref="Map"/>'s catch turns into a plain paragraph. That last
    /// one matters: a <see cref="StackOverflowException"/> is not catchable.
    /// </remarks>
    private const int MaxDepth = 16;

    /// <summary>
    /// Where the walk stops recursing at all, rather than merely stopping preserving structure.
    /// </summary>
    /// <remarks>
    /// Past <see cref="MaxDepth"/> a container is flattened into its text, and flattening is itself
    /// recursive — so without a second bound the descent would be limited only by Markdig's refusal to
    /// parse past 128 levels. Past this one the text is gathered iteratively instead. Nothing is
    /// dropped: losing a reader's words to bound a stack would be the same trade
    /// <see cref="Map"/>'s catch already refuses.
    /// </remarks>
    private const int MaxFlattenDepth = MaxDepth * 2;

    /// <inheritdoc />
    public IReadOnlyList<ExportBlock> Map(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        try
        {
            return MapBlocks(MarkdownPipelines.Parse(markdown), 0);
        }
        catch (Exception)
        {
            // The text is a user prompt or a model answer and belongs in no log, and losing the
            // message entirely is worse than losing its formatting.
            return [new ParagraphBlock([new ExportRun(markdown)])];
        }
    }

    private static List<ExportBlock> MapBlocks(ContainerBlock container, int depth)
    {
        List<ExportBlock> blocks = [];

        foreach (var block in container)
        {
            // A composed email is fenced so the client can offer to open it in a mail client,
            // not because it is code. Exported, it is the message itself and reads as prose —
            // quoted, so a reader can still see where the email starts and stops.
            if (block is FencedCodeBlock fenced && EmailFence.Matches(fenced))
            {
                var lines = EmailFence.Lines(fenced)
                    .Select(ExportBlock (line) => new ParagraphBlock([new ExportRun(line)]))
                    .ToList();

                // An empty fence would otherwise map to nothing at all, and a message whose whole
                // text was one would export blank.
                blocks.Add(lines.Count == 0 ? new ParagraphBlock([]) : new QuoteBlock(lines));
                continue;
            }

            var mapped = MapBlock(block, depth);

            if (mapped is not null)
            {
                blocks.Add(mapped);
            }
        }

        return blocks;
    }

    private static ExportBlock? MapBlock(Block block, int depth) => block switch
    {
        Md.HeadingBlock heading => new HeadingBlock(
            Math.Clamp(heading.Level, 1, 6), MapInlines(heading.Inline)),
        // FencedCodeBlock must precede CodeBlock, which it derives from: only the fenced form carries
        // an info string, and the base arm would swallow it.
        FencedCodeBlock fenced => new CodeBlock(
            string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info, LinesOf(fenced)),
        Md.CodeBlock code => new CodeBlock(null, LinesOf(code)),
        Md.ParagraphBlock paragraph => new ParagraphBlock(MapInlines(paragraph.Inline)),
        Table table when depth < MaxDepth => MapTable(table, depth),
        Md.QuoteBlock quote when depth < MaxDepth => new QuoteBlock(MapBlocks(quote, depth + 1)),
        Md.ListBlock list when depth < MaxDepth => MapList(list, depth),
        Md.ThematicBreakBlock => new ThematicBreakBlock(),
        // A block from an extension this pipeline does not enable. Its text is still worth keeping.
        LeafBlock leaf => new ParagraphBlock(MapInlines(leaf.Inline)),
        // A container past the depth limit: its structure is gone, its text is not.
        ContainerBlock nested when depth < MaxFlattenDepth => new ParagraphBlock(
            [.. MapBlocks(nested, depth + 1).OfType<ParagraphBlock>().SelectMany(paragraph => paragraph.Runs)]),
        // Past the recursion bound the same text is gathered without recursing: Markdig's Descendants
        // walks with an explicit stack, so this terminates at any nesting depth while the message
        // still keeps its words. Styling and links are gone by here, which at thirty-two levels of
        // nesting is not a distinction anything could render.
        ContainerBlock deepest => new ParagraphBlock(FlattenText(deepest)),
        _ => null
    };

    private static IReadOnlyList<ExportRun> FlattenText(ContainerBlock container)
    {
        List<ExportRun> runs = [];

        foreach (var literal in container.Descendants<LiteralInline>())
        {
            Append(runs, literal.Content.ToString(), ExportRunStyles.None, null);
        }

        return Coalesce(runs);
    }

    private static ExportBlock MapList(Md.ListBlock list, int depth)
    {
        List<ListItemBlock> items = [];

        foreach (var child in list)
        {
            if (child is Md.ListItemBlock item)
            {
                items.Add(new ListItemBlock(MapBlocks(item, depth + 1)));
            }
        }

        return new ListBlock(list.IsOrdered, items);
    }

    private static ExportBlock MapTable(Table table, int depth)
    {
        TableRowBlock? header = null;
        List<TableRowBlock> rows = [];

        foreach (var child in table)
        {
            if (child is not TableRow row)
            {
                continue;
            }

            List<IReadOnlyList<ExportRun>> cells = [];

            foreach (var cellBlock in row)
            {
                cells.Add(cellBlock is TableCell cell ? CellRuns(cell, depth) : []);
            }

            var mapped = new TableRowBlock(cells);

            // Markdig marks at most one row as the header, and always the first when it does.
            if (row.IsHeader && header is null)
            {
                header = mapped;
            }
            else
            {
                rows.Add(mapped);
            }
        }

        return new TableBlock(header, rows);
    }

    // A cell is a container of blocks, but every renderer here writes it as one line of runs: a table
    // whose cells hold their own lists or fences is not a table any of these formats can lay out.
    private static IReadOnlyList<ExportRun> CellRuns(TableCell cell, int depth)
    {
        List<ExportRun> runs = [];

        foreach (var block in MapBlocks(cell, depth + 1))
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    runs.AddRange(paragraph.Runs);
                    break;

                case HeadingBlock heading:
                    runs.AddRange(heading.Runs);
                    break;

                case CodeBlock code:
                    runs.Add(new ExportRun(code.Text, ExportRunStyles.Code));
                    break;

                default:
                    break;
            }
        }

        return runs;
    }

    private static string LinesOf(LeafBlock block)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < block.Lines.Count; index++)
        {
            builder.AppendLine(block.Lines.Lines[index].Slice.ToString());
        }

        // The trailing newline is an artefact of writing line by line, not content of the fence.
        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static IReadOnlyList<ExportRun> MapInlines(ContainerInline? container)
    {
        List<ExportRun> runs = [];
        AppendInlines(container, ExportRunStyles.None, null, runs, 0);

        return Coalesce(runs);
    }

    private static void AppendInlines(
        ContainerInline? container, ExportRunStyles styles, string? linkUrl, List<ExportRun> runs, int depth)
    {
        if (container is null || depth > MaxDepth)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Append(runs, literal.Content.ToString(), styles, linkUrl);
                    break;

                case CodeInline code:
                    Append(runs, code.Content ?? string.Empty, styles | ExportRunStyles.Code, linkUrl);
                    break;

                // Two delimiters is strong emphasis and one is ordinary emphasis. Three is both,
                // which Markdig nests as an EmphasisInline inside an EmphasisInline — handled by the
                // recursion rather than by a third case.
                case EmphasisInline emphasis:
                    AppendInlines(
                        emphasis,
                        styles | (emphasis.DelimiterCount >= 2 ? ExportRunStyles.Bold : ExportRunStyles.Italic),
                        linkUrl,
                        runs,
                        depth + 1);
                    break;

                // An image contributes its alt text and nothing else; see the class remarks.
                case LinkInline link:
                    AppendInlines(
                        link,
                        styles,
                        link.IsImage ? linkUrl : NullIfEmpty(link.Url) ?? linkUrl,
                        runs,
                        depth + 1);
                    break;

                case AutolinkInline autolink:
                    Append(
                        runs,
                        autolink.Url,
                        styles,
                        autolink.IsEmail ? $"mailto:{autolink.Url}" : NullIfEmpty(autolink.Url) ?? linkUrl);
                    break;

                // A hard break is a line break inside one paragraph; a soft one is source wrapping,
                // which every target format re-wraps for itself.
                case LineBreakInline lineBreak:
                    Append(runs, lineBreak.IsHard ? "\n" : " ", styles, linkUrl);
                    break;

                case HtmlEntityInline entity:
                    Append(runs, entity.Transcoded.ToString(), styles, linkUrl);
                    break;

                // Raw HTML reaches the tree as escaped text under DisableHtml, so emitting the
                // element here is exactly what the pipeline refuses to do.
                case HtmlInline:
                    break;

                case ContainerInline nested:
                    AppendInlines(nested, styles, linkUrl, runs, depth + 1);
                    break;

                default:
                    break;
            }
        }
    }

    private static void Append(List<ExportRun> runs, string text, ExportRunStyles styles, string? linkUrl)
    {
        if (text.Length > 0)
        {
            runs.Add(new ExportRun(text, styles, linkUrl));
        }
    }

    // Markdig emits a literal per parse event, so ordinary prose arrives as a dozen adjacent runs
    // wearing identical styling. Merging them keeps one Word paragraph from becoming a dozen w:r
    // elements, and keeps a PDF line from being laid out in a dozen measured pieces.
    private static List<ExportRun> Coalesce(List<ExportRun> runs)
    {
        List<ExportRun> merged = [];

        foreach (var run in runs)
        {
            if (merged.Count > 0
                && merged[^1].Styles == run.Styles
                && string.Equals(merged[^1].LinkUrl, run.LinkUrl, StringComparison.Ordinal))
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };

                continue;
            }

            merged.Add(run);
        }

        return merged;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
