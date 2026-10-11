namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// The corpus a single conversation's retrieval tool may search: the conversation's own documents plus,
/// when it belongs to one, its project's documents.
/// </summary>
/// <remarks>
/// Resolved once per turn: per call would re-run authorization for every search and let a mid-turn soft
/// delete change the corpus under a half-answered question.
/// </remarks>
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
