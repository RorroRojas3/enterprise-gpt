namespace Enterprise.Gpt.Service.Converters
{
    /// <summary>
    /// Converts legacy binary Word documents (<c>.doc</c>, Word 97–2003) to Open XML (<c>.docx</c>).
    /// </summary>
    /// <remarks>
    /// Azure Document Intelligence accepts PDF, images and the OOXML Office formats, but not the binary
    /// <c>.doc</c> container. Converting first means <c>.doc</c> uploads take exactly the same OCR path as
    /// every other Word document instead of needing a second, lower-fidelity extraction route.
    /// </remarks>
    public interface ILegacyWordConverter
    {
        /// <summary>
        /// Converts the bytes of a <c>.doc</c> file to the bytes of an equivalent <c>.docx</c> file.
        /// </summary>
        /// <param name="content">The binary <c>.doc</c> content. Must not be <see langword="null"/>.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The converted <c>.docx</c> content.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is <see langword="null"/>.</exception>
        /// <exception cref="FluentValidation.ValidationException">
        /// Thrown when the document is malformed, password-protected, or otherwise cannot be converted.
        /// </exception>
        Task<byte[]> ConvertToDocxAsync(byte[] content, CancellationToken cancellationToken);
    }
}
