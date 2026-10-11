using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// Identifies one chunk within the searchable corpus.
/// </summary>
internal readonly record struct ChunkKey(Guid DocumentId, DocumentSources Source, int Index);
