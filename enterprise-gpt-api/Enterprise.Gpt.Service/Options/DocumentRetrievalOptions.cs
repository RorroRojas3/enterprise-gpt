using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Tuning knobs for the document retrieval (RAG) tool, bound from the <c>Documents:Retrieval</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// The defaults are chosen for the chunk geometry the ingestion pipeline produces —
/// <c>Documents:Chunking</c> at 512 tokens with 128 tokens of overlap. Changing the chunk size
/// without revisiting <see cref="NeighborWindow"/> and <see cref="MaxResultTokens"/> will quietly
/// change how much context each answer is grounded in.
/// </para>
/// <para>
/// Retrieval is exact (kNN) rather than approximate: the candidate set is always narrowed to one
/// conversation and, at most, one project first, which keeps it far below the point where an
/// approximate vector index would pay for itself.
/// </para>
/// </remarks>
public sealed class DocumentRetrievalOptions
{
    /// <summary>
    /// Configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Documents:Retrieval";

    /// <summary>
    /// Number of chunks the vector pass returns before fusion. Default: 40.
    /// </summary>
    /// <remarks>
    /// Over-fetching is what gives fusion and the per-document cap something to choose between;
    /// retrieving exactly <see cref="MaxResults"/> would make both a no-op.
    /// </remarks>
    [Range(1, 500)]
    public int CandidateCount { get; set; } = 40;

    /// <summary>
    /// Number of chunks the keyword pass returns before fusion. Default: 40.
    /// </summary>
    [Range(1, 500)]
    public int LexicalCandidateCount { get; set; } = 40;

    /// <summary>
    /// Largest number of passages handed back to the model in one search. Default: 8.
    /// </summary>
    [Range(1, 50)]
    public int MaxResults { get; set; } = 8;

    /// <summary>
    /// Number of chunks pulled either side of a matching chunk. Default: 1.
    /// </summary>
    /// <remarks>
    /// A 512-token chunk regularly cuts an explanation in half. Pulling the neighbours back restores
    /// the sentences on the other side of the cut, and because chunks overlap by 128 tokens the seam
    /// is removed rather than duplicated when they are merged. Zero disables expansion.
    /// </remarks>
    [Range(0, 5)]
    public int NeighborWindow { get; set; } = 1;

    /// <summary>
    /// Largest cosine distance a vector-only match may have and still be returned. Default: 0.62.
    /// </summary>
    /// <remarks>
    /// <c>VECTOR_DISTANCE('cosine', …)</c> returns a distance in [0, 2], not a similarity, so smaller
    /// is closer and 0.62 corresponds to a cosine similarity of about 0.38. The gate exists because
    /// nearest-neighbour search always returns <em>something</em>: without it a question the documents
    /// do not answer comes back with the least-bad chunks, which reads exactly like grounded evidence.
    /// </remarks>
    [Range(0.0, 2.0)]
    public double MaxDistance { get; set; } = 0.62;

    /// <summary>
    /// Largest number of passages any single document may contribute to one search. Default: 3.
    /// </summary>
    [Range(1, 50)]
    public int MaxPassagesPerDocument { get; set; } = 3;

    /// <summary>
    /// Token budget for the passage text of one search result. Default: 3000.
    /// </summary>
    /// <remarks>
    /// Counted with the ingestion tokenizer, so it is measured the same way the chunks were.
    /// Every token here is replayed to the model on the tool result, and again in the transcript of
    /// nothing — the result is not persisted — but it does compete with the answer for the model's
    /// context window.
    /// </remarks>
    [Range(256, 32000)]
    public int MaxResultTokens { get; set; } = 3000;

    /// <summary>
    /// Largest number of query terms the keyword pass will search for. Default: 8.
    /// </summary>
    [Range(1, 16)]
    public int MaxQueryTerms { get; set; } = 8;

    /// <summary>
    /// Command timeout for a retrieval query, in seconds. Default: 15.
    /// </summary>
    /// <remarks>
    /// Retrieval runs inside a live chat turn, so a query that degrades has to fail fast rather than
    /// hold the stream open behind the provider's own timeout.
    /// </remarks>
    [Range(1, 120)]
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Whether the keyword pass runs alongside the vector pass. Default: <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Embeddings are weak at exact tokens — part numbers, error codes, acronyms, surnames — which is
    /// a large share of what people search enterprise documents for. Turning this off falls back to
    /// vector-only retrieval.
    /// </remarks>
    public bool EnableLexicalSearch { get; set; } = true;
}
