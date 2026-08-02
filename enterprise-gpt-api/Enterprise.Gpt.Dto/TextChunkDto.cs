namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// A single retrieval unit produced by the chunker: a token-bounded span of document text.
    /// </summary>
    public class TextChunkDto
    {
        /// <summary>
        /// Zero-based ordinal of this chunk within the document, in reading order.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// The page or slide the chunk starts on, or <see langword="null"/> when the source format has no
        /// such concept.
        /// </summary>
        public int? SourceNumber { get; set; }

        /// <summary>
        /// Number of tokens in <see cref="Text"/>, as counted by the embedding model's tokenizer.
        /// </summary>
        public int TokenCount { get; set; }

        /// <summary>
        /// The chunk text to embed.
        /// </summary>
        public string Text { get; set; } = null!;
    }
}
