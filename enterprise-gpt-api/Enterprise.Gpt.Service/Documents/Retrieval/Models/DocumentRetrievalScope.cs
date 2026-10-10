namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// The corpus a single conversation's retrieval tool may search: the conversation's own documents plus,
/// when it belongs to one, its project's documents.
/// </summary>
/// <remarks>
/// Resolved once per turn, before the stream starts, and captured by the tool. Resolving it per tool
/// call would re-run the authorization query for every search the model performs, and would let a
/// document soft-deleted mid-turn change the corpus underneath a half-answered question.
/// </remarks>
/// <param name="ConversationId">The conversation being answered.</param>
/// <param name="ProjectId">The conversation's project, or <see langword="null"/> when it is standalone.</param>
/// <param name="Documents">The searchable documents, empty when there are none.</param>
public sealed record DocumentRetrievalScope(
    Guid ConversationId,
    Guid? ProjectId,
    IReadOnlyList<RetrievableDocument> Documents)
{
    /// <summary>
    /// Gets a value indicating whether there is anything to search.
    /// </summary>
    public bool HasDocuments => Documents.Count > 0;

    /// <summary>
    /// Gets the distinct document names, in scope order.
    /// </summary>
    public IReadOnlyList<string> DocumentNames => [.. Documents.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
}
