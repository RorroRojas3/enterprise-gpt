using RR.AI_Chat.Dto;

namespace RR.AI_Chat.Service.Common.Interface
{
    public interface IFileService
    {
        /// <summary>
        /// Extracts plain text from each page of the provided Word document bytes.
        /// </summary>
        /// <param name="bytes">The binary contents of the Word document to process.</param>
        /// <param name="fileName">A friendly name of the document used for logging.</param>
        /// <returns>
        /// A list of <see cref="DocumentExtractorDto"/> where each item contains the one-based page number
        /// and its extracted plain text.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="bytes"/> or <paramref name="fileName"/> is <c>null</c>.
        /// </exception>
        /// <remarks>
        /// Uses Aspose.Words pagination to iterate per page and converts each page to text using <see cref="SaveFormat.Text"/>.
        /// </remarks>
        List<DocumentExtractorDto> ExtractText(byte[] bytes, string fileName);
    }
}
