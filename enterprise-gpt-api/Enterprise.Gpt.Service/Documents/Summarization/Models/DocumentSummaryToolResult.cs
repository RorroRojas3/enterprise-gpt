using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// The result of one <c>document_summarize</c> call, serialized straight back to the model.
/// </summary>
public sealed class DocumentSummaryToolResult
{
    /// <summary>
    /// Gets or sets what was summarized: <c>document</c> for a single file, <c>digest</c> for every
    /// document the conversation can see.
    /// </summary>
    [JsonPropertyOrder(0)]
    public required string Scope { get; set; }

    /// <summary>
    /// Gets or sets the name of the document that was summarized, or <see langword="null"/> for a
    /// digest.
    /// </summary>
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentName { get; set; }

    /// <summary>
    /// Gets or sets how many documents the summary covers.
    /// </summary>
    [JsonPropertyOrder(2)]
    public int DocumentCount { get; set; }

    /// <summary>
    /// Gets or sets the summary text, or <see langword="null"/> when no summary was produced
    /// because the requested document could not be identified.
    /// </summary>
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>
    /// Gets or sets the names of the documents available to summarize. Populated only when
    /// <see cref="Summary"/> is absent, so the model can name one correctly on a second attempt.
    /// </summary>
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AvailableDocuments { get; set; }

    /// <summary>
    /// Gets or sets a short explanation of an absent or unusual result, for the model rather than
    /// the user.
    /// </summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
