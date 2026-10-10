using System.Text;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// Identifies one chunk within the searchable corpus.
/// </summary>
/// <param name="DocumentId">The document the chunk belongs to.</param>
/// <param name="Source">Which table the chunk lives in.</param>
/// <param name="Index">The chunk's zero-based ordinal within its document.</param>
internal readonly record struct ChunkKey(Guid DocumentId, DocumentSources Source, int Index);
