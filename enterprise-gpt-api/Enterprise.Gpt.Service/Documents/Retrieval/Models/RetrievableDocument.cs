using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// One document the retrieval tool is allowed to search in the current turn.
/// </summary>
public readonly record struct RetrievableDocument(Guid Id, DocumentSources Source, string Name);
