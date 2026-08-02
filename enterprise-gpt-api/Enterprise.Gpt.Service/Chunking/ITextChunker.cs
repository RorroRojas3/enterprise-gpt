using Enterprise.Gpt.Dto;

namespace Enterprise.Gpt.Service.Chunking
{
    /// <summary>
    /// Repacks extracted document segments into token-bounded, overlapping chunks suitable for embedding.
    /// </summary>
    public interface ITextChunker
    {
        /// <summary>
        /// Maximum number of tokens any produced chunk may contain.
        /// </summary>
        int MaxTokens { get; }

        /// <summary>
        /// Number of tokens carried from the end of one chunk into the start of the next.
        /// </summary>
        int OverlapTokens { get; }

        /// <summary>
        /// Counts the tokens in <paramref name="text"/> using the embedding model's tokenizer.
        /// </summary>
        /// <param name="text">The text to measure.</param>
        /// <returns>The number of tokens.</returns>
        int CountTokens(string text);

        /// <summary>
        /// Splits <paramref name="segments"/> into ordered, overlapping chunks.
        /// </summary>
        /// <param name="segments">The extracted segments, in reading order.</param>
        /// <param name="cancellationToken">A token to observe while chunking a large document.</param>
        /// <returns>
        /// The chunks in reading order with dense, zero-based <see cref="TextChunkDto.Index"/> values.
        /// Empty when <paramref name="segments"/> contains no readable text.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="segments"/> is <see langword="null"/>.</exception>
        IReadOnlyList<TextChunkDto> Chunk(IReadOnlyList<DocumentSegmentDto> segments, CancellationToken cancellationToken);
    }
}
