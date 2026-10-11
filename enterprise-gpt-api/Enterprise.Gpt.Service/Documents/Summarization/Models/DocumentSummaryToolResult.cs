using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// The result of one <c>document_summarize</c> call, serialized straight back to the model.
/// </summary>
public sealed class DocumentSummaryToolResult
{
    /// <summary><c>document</c> for a single file, <c>digest</c> for every document in scope.</summary>
    [JsonPropertyOrder(0)]
    public required string Scope { get; set; }

    /// <summary><see langword="null"/> for a digest.</summary>
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentName { get; set; }

    [JsonPropertyOrder(2)]
    public int DocumentCount { get; set; }

    /// <summary><see langword="null"/> when the requested document could not be identified.</summary>
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>
    /// Populated only when <see cref="Summary"/> is absent, so the model can name a document on a retry.
    /// </summary>
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AvailableDocuments { get; set; }

    /// <summary>Explains an absent or unusual result, for the model rather than the user.</summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
