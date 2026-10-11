using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// A chunk that matched one retrieval pass, before fusion. <c>Distance</c> is null for a keyword match and
/// <c>MatchCount</c> zero for a vector match.
/// </summary>
internal readonly record struct ChunkCandidate(
    Guid DocumentId,
    DocumentSources Source,
    int Index,
    double? Distance,
    int MatchCount);
