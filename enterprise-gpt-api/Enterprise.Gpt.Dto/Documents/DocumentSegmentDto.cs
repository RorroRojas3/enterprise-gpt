namespace Enterprise.Gpt.Dto.Documents;

/// <summary>
/// A contiguous span of text produced by a document text extractor, before chunking.
/// </summary>
/// <remarks>
/// The format's natural division: a page, slide, sheet row window or schema card, or the whole text file.
/// Not the retrieval unit — the chunker repacks segments, which survive only as provenance.
/// </remarks>
public class DocumentSegmentDto
{
    /// <summary>
    /// The 1-based page or slide number this segment came from, or <see langword="null"/> for formats
    /// with no such concept.
    /// </summary>
    public int? SourceNumber { get; set; }

    public string Text { get; set; } = null!;
}
