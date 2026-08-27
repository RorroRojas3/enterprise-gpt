using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// Which table a retrievable document and its chunks live in.
/// </summary>
/// <remarks>
/// Persisted nowhere: it exists only to keep the two structurally identical chunk tables apart once
/// their rows have been unioned into a single candidate set, so a conversation document and a project
/// document that happen to share a chunk index are never confused for one another.
/// </remarks>
public enum DocumentSource : byte
{
    /// <summary>A document uploaded into the conversation itself.</summary>
    Conversation = 0,

    /// <summary>A document uploaded into the conversation's project.</summary>
    Project = 1
}

/// <summary>
/// One document the retrieval tool is allowed to search in the current turn.
/// </summary>
/// <param name="Id">The document's unique identifier.</param>
/// <param name="Source">Which table the document and its chunks live in.</param>
/// <param name="Name">The document's file name, used for citation and for name filtering.</param>
public readonly record struct RetrievableDocument(Guid Id, DocumentSource Source, string Name);

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

/// <summary>
/// A contiguous span of one document's text that matched a search, already merged across the chunks
/// it spans and with the chunker's overlap removed.
/// </summary>
public sealed class RetrievedPassage
{
    /// <summary>
    /// Gets or sets a short human-readable source reference the model can quote verbatim, such as
    /// <c>handbook.pdf p.12</c> or <c>budget.xlsx — Regional Revenue</c>.
    /// </summary>
    [JsonPropertyOrder(0)]
    public required string Citation { get; set; }

    /// <summary>
    /// Gets or sets the file name of the document the passage came from.
    /// </summary>
    [JsonPropertyOrder(1)]
    public required string DocumentName { get; set; }

    /// <summary>
    /// Gets or sets the page, slide, or sheet ordinal the passage starts on, or <see langword="null"/>
    /// for formats with no such concept (<c>.doc</c>, <c>.md</c>, <c>.txt</c>).
    /// </summary>
    /// <remarks>
    /// For a spreadsheet this is the sheet's position, not a page, which is why
    /// <see cref="Citation"/> names the sheet instead of rendering this as <c>p.N</c>.
    /// </remarks>
    [JsonPropertyOrder(2)]
    public int? Page { get; set; }

    /// <summary>
    /// Gets or sets the similarity of the best-matching chunk in the passage, from 0 to 1, where higher
    /// is closer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported as a similarity rather than the cosine <em>distance</em> the database returns, because a
    /// score the model is asked to reason about should get larger as relevance grows.
    /// </para>
    /// <para>
    /// <see cref="DocumentSearchResult.Results"/> is <em>not</em> sorted by this. Results carry the fused
    /// rank of both retrieval passes, so a passage found by its exact terms can sit first with the lowest
    /// score of the set — this measures wording similarity alone and knows nothing about term matches.
    /// </para>
    /// </remarks>
    [JsonPropertyOrder(3)]
    public double Score { get; set; }

    /// <summary>
    /// Gets or sets the passage text.
    /// </summary>
    [JsonPropertyOrder(4)]
    public required string Text { get; set; }
}

/// <summary>
/// The result of one <c>document_search</c> call, serialized straight back to the model.
/// </summary>
/// <remarks>
/// An empty result is a normal outcome, not a failure: a question the documents do not answer has to be
/// answerable with "they do not say", and the model can only conclude that if the tool returns cleanly.
/// </remarks>
public sealed class DocumentSearchResult
{
    /// <summary>
    /// Gets or sets the query that was searched for, echoed so the model can tell several searches apart.
    /// </summary>
    [JsonPropertyOrder(0)]
    public required string Query { get; set; }

    /// <summary>
    /// Gets or sets the number of passages in <see cref="Results"/>.
    /// </summary>
    [JsonPropertyOrder(1)]
    public int ResultCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether matching passages were dropped to stay inside the token
    /// budget, so the model knows a narrower query could surface more.
    /// </summary>
    [JsonPropertyOrder(2)]
    public bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets the matching passages, most relevant first.
    /// </summary>
    [JsonPropertyOrder(3)]
    public IReadOnlyList<RetrievedPassage> Results { get; set; } = [];

    /// <summary>
    /// Gets or sets the names of the documents that were searched. Populated only when
    /// <see cref="Results"/> is empty.
    /// </summary>
    /// <remarks>
    /// This is what lets the model recover from a bad query instead of asserting the documents are
    /// silent: it can see what it could have searched and try different terms.
    /// </remarks>
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AvailableDocuments { get; set; }

    /// <summary>
    /// Gets or sets a short explanation of an empty or unusual result, for the model rather than the user.
    /// </summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
