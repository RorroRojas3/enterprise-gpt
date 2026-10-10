namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// A contiguous span of text produced by a document text extractor, before chunking.
    /// </summary>
    /// <remarks>
    /// A segment is whatever natural division the source format offers — a page for PDF and Word, a slide
    /// for PowerPoint, a row window or a schema card for a sheet, the whole file for plain text and
    /// Markdown.
    /// Segments are <em>not</em> the retrieval unit; the chunker repacks them into token-sized chunks.
    /// Their only lasting role is provenance.
    /// </remarks>
    public class DocumentSegmentDto
    {
        /// <summary>
        /// The 1-based page or slide number this segment came from, or <see langword="null"/> for formats
        /// with no such concept.
        /// </summary>
        public int? SourceNumber { get; set; }

        /// <summary>
        /// The extracted text of the segment.
        /// </summary>
        public string Text { get; set; } = null!;
    }
}
