using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// One document the retrieval tool is allowed to search in the current turn.
/// </summary>
/// <param name="Id">The document's unique identifier.</param>
/// <param name="Source">Which table the document and its chunks live in.</param>
/// <param name="Name">The document's file name, used for citation and for name filtering.</param>
public readonly record struct RetrievableDocument(Guid Id, DocumentSources Source, string Name);
