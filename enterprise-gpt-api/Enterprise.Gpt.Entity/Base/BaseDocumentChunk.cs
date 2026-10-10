using Microsoft.Data.SqlTypes;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    /// <summary>
    /// Base type for a single retrieval unit of an ingested document: a span of text sized for the
    /// embedding model, together with its vector.
    /// </summary>
    /// <remarks>
    /// Replaces the former <c>BaseDocumentPage</c>. Pages were a poor retrieval unit — a long page could
    /// exceed the embedding model's input limit, and a sparse one produced an almost empty vector. Chunks
    /// are produced by the token-based chunker and deliberately overlap so a fact split across a boundary
    /// still appears whole in at least one chunk.
    /// </remarks>
    public class BaseDocumentChunk : BaseModifiedEntity
    {
        /// <summary>
        /// Zero-based ordinal of this chunk within its document, in reading order.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// The page (PDF, Word) or slide (PowerPoint) the chunk starts on, for provenance and citation.
        /// <see langword="null"/> for formats with no such concept (<c>.doc</c>, <c>.md</c>, <c>.txt</c>).
        /// </summary>
        public int? SourceNumber { get; set; }

        /// <summary>
        /// Number of tokens in <see cref="Text"/> as counted by the embedding model's tokenizer.
        /// </summary>
        public int TokenCount { get; set; }

        /// <summary>
        /// The chunk text that was embedded.
        /// </summary>
        public string Text { get; set; } = null!;

        /// <summary>
        /// The embedding vector for <see cref="Text"/>. The dimension is fixed by the column type and must
        /// match the configured embedding model.
        /// </summary>
        [Column(TypeName = "vector(1536)")]
        public SqlVector<float> Embedding { get; set; }
    }
}
