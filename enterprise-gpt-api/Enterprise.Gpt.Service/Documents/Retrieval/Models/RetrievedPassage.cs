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
