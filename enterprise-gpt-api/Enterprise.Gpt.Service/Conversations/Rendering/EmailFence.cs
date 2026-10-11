using Markdig.Syntax;

namespace Enterprise.Gpt.Service.Conversations.Rendering;

/// <summary>
/// The fenced block the assistant marks a composed email with.
/// </summary>
/// <remarks>
/// Shared by the HTML renderer and the export mapper, and the client matches this exact spelling to offer
/// its mail-client control, so any disagreement renders an email in one surface and code in another.
/// </remarks>
public static class EmailFence
{
    /// <summary>
    /// The info string, matched case-insensitively.
    /// </summary>
    public const string Info = "email";

    #region Public static methods

    /// <summary>Whether a fenced block's whole info string is <see cref="Info"/>.</summary>
    /// <remarks>
    /// <c>```email draft</c> deliberately does not match: the client requires the entire string, and a
    /// wider match would render the message as prose in an export but as code in the chat.
    /// </remarks>
    public static bool Matches(FencedCodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return string.IsNullOrEmpty(block.Arguments)
            && string.Equals(block.Info, Info, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The block's non-blank lines, in order.</summary>
    /// <remarks>
    /// Per line, not per paragraph, so header lines and a sign-off are not run together; a hard-wrapped
    /// body becomes separate paragraphs as a result.
    /// </remarks>
    public static IEnumerable<string> Lines(LeafBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        // Split so the guard throws at the call, not on first enumeration.
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

    #endregion
}
