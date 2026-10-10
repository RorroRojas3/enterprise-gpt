namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// Builds the download file name an export is served under.
/// </summary>
public static class ExportFileNames
{
    private const int MaxStemLength = 60;

    /// <summary>
    /// Reduces a conversation name to something safe to put in a file name.
    /// </summary>
    /// <param name="name">The conversation's display name.</param>
    /// <returns>
    /// The name with every character that is not a letter, a digit, a hyphen or an underscore replaced
    /// by a hyphen, trimmed and truncated — or <c>conversation</c> when nothing usable is left.
    /// </returns>
    /// <remarks>
    /// Deliberately stricter than <c>Content-Disposition</c> requires. The header is written with an
    /// RFC 5987 <c>filename*</c> that could carry the name verbatim, but the file lands on a reader's
    /// disk and on whatever they attach it to next, and a name built from model-influenced text should
    /// not be the thing that decides what a path separator means there.
    /// </remarks>
    public static string Stem(string name)
    {
        var stem = new string([.. name.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')]).Trim('-');

        return string.IsNullOrEmpty(stem) ? "conversation" : stem[..Math.Min(stem.Length, MaxStemLength)];
    }
}
