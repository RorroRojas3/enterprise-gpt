namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// A chunk with its fused rank-fusion score (higher is better) and the evidence each pass contributed:
/// <c>Distance</c> is null if only the keyword pass found it, <c>MatchCount</c> zero if only the vector one.
/// </summary>
internal readonly record struct ScoredChunk(ChunkKey Key, double Score, double? Distance, int MatchCount);
