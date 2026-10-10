using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

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
