using System.Text;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// A chunk's text and provenance, read back once retrieval has decided it is worth showing.
/// </summary>
/// <param name="Key">Which chunk this is.</param>
/// <param name="SourceNumber">The page or slide the chunk starts on, when the format has one.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="Distance">
/// The chunk's own cosine distance from the query vector. Recomputed on read-back rather than carried
/// from the pass that selected it, so a chunk found only by keyword — and a neighbour pulled in for
/// context — still has a comparable score.
/// </param>
/// <remarks>
/// The stored <c>TokenCount</c> is deliberately not read: merging strips the overlap between chunks, so
/// a passage is shorter than the sum of its parts and the budget has to measure the merged text.
/// </remarks>
internal sealed record ChunkText(ChunkKey Key, int? SourceNumber, string Text, double Distance);
