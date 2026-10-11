using Microsoft.Data.SqlTypes;

namespace Enterprise.Gpt.Entity.Base;

/// <summary>
/// Base type for a single retrieval unit of an ingested document: a span of text sized for the
/// embedding model, together with its vector.
/// </summary>
/// <remarks>
/// Chunks deliberately overlap, so a fact split across a boundary still appears whole in at least one chunk.
/// </remarks>
/// <typeparam name="TKey">The key type, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
public abstract class BaseDocumentChunk<TKey> : BaseModifiedEntity<TKey>
    where TKey : struct, IEquatable<TKey>
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

    public string Text { get; set; } = null!;

    /// <remarks>The dimension is fixed by the column type and must match the configured embedding model.</remarks>
    public SqlVector<float> Embedding { get; set; }
}
