using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

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
    [JsonPropertyOrder(2)]
    public int? Page { get; set; }

    /// <summary>
    /// Gets or sets the similarity of the best-matching chunk in the passage, from 0 to 1, where higher
    /// is closer.
    /// </summary>
    /// <remarks>
    /// <see cref="DocumentSearchResult.Results"/> is not sorted by this: it measures wording similarity only,
    /// while results follow the fused rank of both passes.
    /// </remarks>
    [JsonPropertyOrder(3)]
    public double Score { get; set; }

    /// <summary>
    /// Gets or sets the passage text.
    /// </summary>
    [JsonPropertyOrder(4)]
    public required string Text { get; set; }
}
