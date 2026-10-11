using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Conversations.Rendering;
using Enterprise.Gpt.Service.Exports.Models;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CodeBlock = Enterprise.Gpt.Service.Exports.Models.CodeBlock;
using HeadingBlock = Enterprise.Gpt.Service.Exports.Models.HeadingBlock;
using ListBlock = Enterprise.Gpt.Service.Exports.Models.ListBlock;
using ListItemBlock = Enterprise.Gpt.Service.Exports.Models.ListItemBlock;
using Md = Markdig.Syntax;
using ParagraphBlock = Enterprise.Gpt.Service.Exports.Models.ParagraphBlock;
using QuoteBlock = Enterprise.Gpt.Service.Exports.Models.QuoteBlock;
using ThematicBreakBlock = Enterprise.Gpt.Service.Exports.Models.ThematicBreakBlock;

namespace Enterprise.Gpt.Service.Exports;

/// <summary>
/// Turns a message's markdown into the renderer-neutral block model.
/// </summary>
public interface IMarkdownBlockMapper
{
    /// <summary>
    /// Maps markdown into blocks.
    /// </summary>
    /// <returns>The blocks in document order; empty when nothing is renderable.</returns>
    /// <remarks>
    /// Never throws: an unparseable message degrades to one paragraph of its own text.
    /// </remarks>
    IReadOnlyList<ExportBlock> Map(string? markdown);
}

/// <summary>
/// Walks Markdig's syntax tree into <see cref="ExportBlock"/>s.
/// </summary>
/// <remarks>
/// Parsed through <see cref="MarkdownPipelines.Parse"/> so the link-scheme allowlist and raw-HTML refusal
/// apply here too; a <c>javascript:</c> link is live in Word and most PDF readers. Images are dropped (alt
/// text kept): fetching a model-chosen URL from the API's network position would be an SSRF surface.
/// </remarks>
public sealed class MarkdownBlockMapper : IMarkdownBlockMapper
{
    /// <summary>
    /// Fidelity limit: deeper containers keep their text but lose their structure. Markdig itself refuses
    /// input past 128 levels (pinned 1.3.2), which <see cref="Map"/>'s catch absorbs.
    /// </summary>
    private const int MaxDepth = 16;

    /// <summary>
    /// Past this, text is gathered iteratively rather than by recursive flattening, because a
    /// <see cref="StackOverflowException"/> cannot be caught. No words are dropped.
    /// </summary>
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

    #region Private methods

    private static List<ExportBlock> MapBlocks(ContainerBlock container, int depth)
    {
        List<ExportBlock> blocks = [];

        foreach (var block in container)
        {
            // An email is fenced only so the client can offer a mail client; exported, it reads as prose,
            // quoted to show where it starts and stops.
            if (block is FencedCodeBlock fenced && EmailFence.Matches(fenced))
            {
                var lines = EmailFence.Lines(fenced)
                    .Select(ExportBlock (line) => new ParagraphBlock([new ExportRun(line)]))
                    .ToList();

                // Otherwise an empty fence maps to nothing, and a message that was only one exports blank.
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
        // Past the recursion bound: Descendants walks with an explicit stack, so any depth terminates with
        // its words intact, though styling and links are lost.
        ContainerBlock deepest => new ParagraphBlock(FlattenText(deepest)),
        _ => null
    };

    private static List<ExportRun> FlattenText(ContainerBlock container)
    {
        List<ExportRun> runs = [];

        foreach (var literal in container.Descendants<LiteralInline>())
        {
            Append(runs, literal.Content.ToString(), ExportRunStyles.None, null);
        }

        return Coalesce(runs);
    }

    private static ListBlock MapList(Md.ListBlock list, int depth)
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

    private static TableBlock MapTable(Table table, int depth)
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
    private static List<ExportRun> CellRuns(TableCell cell, int depth)
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

    private static List<ExportRun> MapInlines(ContainerInline? container)
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

                // Triple emphasis nests as EmphasisInline in EmphasisInline, so the recursion covers it.
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

                // A soft break is source wrapping, which every target format re-wraps for itself.
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

    // Markdig emits a literal per parse event; merging same-styled runs avoids a dozen w:r elements per
    // Word paragraph and a dozen measured pieces per PDF line.
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

    #endregion
}
