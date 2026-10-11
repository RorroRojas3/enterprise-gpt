namespace Enterprise.Gpt.Service.Exports;

/// <summary>
/// Builds the download file name an export is served under.
/// </summary>
public static class ExportFileNames
{
    private const int MaxStemLength = 60;

    #region Public static methods

    /// <summary>
    /// Reduces a conversation name to something safe to put in a file name.
    /// </summary>
    /// <remarks>
    /// Stricter than <c>Content-Disposition</c> needs: the file lands on disks and attachments, where
    /// model-influenced text should not decide what a path separator means.
    /// </remarks>
    public static string Stem(string name)
    {
        var stem = new string([.. name.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')]).Trim('-');

        return string.IsNullOrEmpty(stem) ? "conversation" : stem[..Math.Min(stem.Length, MaxStemLength)];
    }

    #endregion
}
