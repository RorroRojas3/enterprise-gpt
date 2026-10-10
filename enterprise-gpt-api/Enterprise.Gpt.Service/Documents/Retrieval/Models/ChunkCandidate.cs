using System.Text;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// A chunk that matched one retrieval pass, before fusion.
/// </summary>
/// <param name="DocumentId">The document the chunk belongs to.</param>
/// <param name="Source">Which table the chunk lives in.</param>
/// <param name="Index">The chunk's zero-based ordinal within its document.</param>
/// <param name="Distance">The cosine distance from the query vector, or <see langword="null"/> for a keyword match.</param>
/// <param name="MatchCount">The number of query terms present in the chunk, or zero for a vector match.</param>
internal readonly record struct ChunkCandidate(
    Guid DocumentId,
    DocumentSources Source,
    int Index,
    double? Distance,
    int MatchCount);
