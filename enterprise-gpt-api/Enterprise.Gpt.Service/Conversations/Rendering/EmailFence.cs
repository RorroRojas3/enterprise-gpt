using Markdig.Syntax;

namespace Enterprise.Gpt.Service.Rendering;

/// <summary>
/// The fenced block the assistant marks a composed email with.
/// </summary>
/// <remarks>
/// One definition for the whole server, because the fence means the same thing to the HTML renderer
/// and to the export mapper, and because the client matches this exact spelling to decide whether to
/// offer its mail-client control — a fence the two sides disagree about is an email in one surface
/// and a code block in the other.
/// </remarks>
public static class EmailFence
{
    /// <summary>
    /// The info string, matched case-insensitively.
    /// </summary>
    public const string Info = "email";

    /// <summary>
    /// Whether a fenced block is a composed email.
    /// </summary>
    /// <param name="block">The block to test.</param>
    /// <returns><see langword="true"/> when the block's whole info string is <see cref="Info"/>.</returns>
    /// <remarks>
    /// Markdig splits an info string at the first space, so <c>```email draft</c> parses as info
    /// <c>email</c> with arguments <c>draft</c>. That is deliberately <em>not</em> a match: the
    /// client requires the entire string, and recognising a wider set here would render the same
    /// message as prose in an export and as code in the chat.
    /// </remarks>
    public static bool Matches(FencedCodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return string.IsNullOrEmpty(block.Arguments)
            && string.Equals(block.Info, Info, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The block's content, one entry per non-blank line.
    /// </summary>
    /// <param name="block">The fenced block.</param>
    /// <returns>The lines, in order.</returns>
    /// <remarks>
    /// Per line rather than per blank-line-separated paragraph, because the header lines and a
    /// sign-off are single lines that must not be run together. A body the model hard wrapped is the
    /// case this reads worse for: its wrapped lines become separate paragraphs.
    /// </remarks>
    public static IEnumerable<string> Lines(LeafBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        // Split so the guard runs at the call rather than at the first enumeration, which is where
        // an iterator method would otherwise defer it to.
        return Iterate(block);

        static IEnumerable<string> Iterate(LeafBlock block)
        {
            for (var index = 0; index < block.Lines.Count; index++)
            {
                var line = block.Lines.Lines[index].Slice.ToString();

                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line;
                }
            }
        }
    }
}
