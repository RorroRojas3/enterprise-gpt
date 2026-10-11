namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// A chunk's text and provenance, read back once retrieval has decided it is worth showing.
/// </summary>
/// <remarks>
/// <c>Distance</c> is recomputed on read-back so keyword-only matches and neighbours are comparable.
/// <c>TokenCount</c> is not read: merging strips overlap, so the budget measures the merged text.
/// </remarks>
internal sealed record ChunkText(ChunkKey Key, int? SourceNumber, string Text, double Distance);
