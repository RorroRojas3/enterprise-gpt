using System.Globalization;
using System.Text;
using Enterprise.Gpt.Service.Prompts.Models;

namespace Enterprise.Gpt.Service.Prompts;

/// <summary>Loads and parameterises the document summarization prompts in the <c>Prompts/</c> templates.</summary>
/// <remarks>
/// The document rides the user message, not the instructions, because the budget counts the framing as overhead
/// and measures only the message. No document name reaches the framing: names are attacker-chosen and project
/// documents are shared, so the digest's labels ride inside the fence.
/// </remarks>
public static class SummarizationPrompts
{
    /// <summary>The prompt generation a summary was produced under, persisted on each summary row.</summary>
    /// <remarks>Bump whenever a template change would change what a model returns.</remarks>
    public const string PromptVersion = "2";

    private static readonly CompositeFormat SinglePassTemplate =
        CompositeFormat.Parse(PromptTemplateLoader.Load("document-summary-single-pass-prompt.md"));

    private static readonly CompositeFormat MapTemplate =
        CompositeFormat.Parse(PromptTemplateLoader.Load("document-summary-map-prompt.md"));

    private static readonly CompositeFormat ReduceTemplate =
        CompositeFormat.Parse(PromptTemplateLoader.Load("document-summary-reduce-prompt.md"));

    private static readonly CompositeFormat CollapseTemplate =
        CompositeFormat.Parse(PromptTemplateLoader.Load("document-summary-collapse-prompt.md"));

    private static readonly CompositeFormat DigestTemplate =
        CompositeFormat.Parse(PromptTemplateLoader.Load("document-summary-digest-prompt.md"));

    #region Private methods

    /// <remarks>Numbered inside one fence so the model sees the single trust boundary it was told about.</remarks>
    private static SummarizationPrompt BuildNumberedParts(
        CompositeFormat template,
        IReadOnlyList<string> partialSummaries)
    {
        ArgumentNullException.ThrowIfNull(partialSummaries);

        if (partialSummaries.Count == 0)
        {
            throw new ArgumentException(
                "At least one part summary is required to reduce.", nameof(partialSummaries));
        }

        var delimiter = CreateDelimiter();
        var body = new StringBuilder();

        for (var i = 0; i < partialSummaries.Count; i++)
        {
            if (i > 0)
            {
                body.Append("\n\n");
            }

            body.Append(CultureInfo.InvariantCulture, $"Part {i + 1} of {partialSummaries.Count}:\n")
                .Append(partialSummaries[i]);
        }

        return new SummarizationPrompt(
            string.Format(CultureInfo.InvariantCulture, template, delimiter),
            Fence(delimiter, body.ToString()));
    }

    /// <remarks>Random per call because a fixed delimiter could be reproduced inside the untrusted body.</remarks>
    private static string CreateDelimiter() => $"<<<document-text:{Guid.NewGuid():N}>>>";

    private static string Fence(string delimiter, string content) =>
        $"{delimiter}\n{content}\n{delimiter}";

    #endregion

    #region Public static methods

    /// <summary>Builds the prompt for a document that fits the summarizer's budget in one call.</summary>
    /// <exception cref="ArgumentException"><paramref name="documentText"/> is null or whitespace.</exception>
    public static SummarizationPrompt BuildSinglePass(string documentText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentText);

        var delimiter = CreateDelimiter();

        return new SummarizationPrompt(
            string.Format(CultureInfo.InvariantCulture, SinglePassTemplate, delimiter),
            Fence(delimiter, documentText));
    }

    /// <summary>Builds the prompt for one map unit of an oversized document.</summary>
    /// <exception cref="ArgumentException"><paramref name="unitText"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="unitNumber"/> is not between one and <paramref name="unitCount"/>.
    /// </exception>
    public static SummarizationPrompt BuildMap(int unitNumber, int unitCount, string unitText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitText);
        ArgumentOutOfRangeException.ThrowIfLessThan(unitNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(unitNumber, unitCount);

        var delimiter = CreateDelimiter();

        return new SummarizationPrompt(
            string.Format(CultureInfo.InvariantCulture, MapTemplate, delimiter, unitNumber, unitCount),
            Fence(delimiter, unitText));
    }

    /// <summary>Builds the prompt that assembles in-order part summaries into the final summary.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="partialSummaries"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="partialSummaries"/> is empty.</exception>
    public static SummarizationPrompt BuildReduce(IReadOnlyList<string> partialSummaries) =>
        BuildNumberedParts(ReduceTemplate, partialSummaries);

    /// <summary>Builds the prompt that shortens part summaries until they fit one reduce call.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="partialSummaries"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="partialSummaries"/> is empty.</exception>
    /// <remarks>
    /// Separate from <see cref="BuildReduce"/> because a collapse must come back shorter for the loop to
    /// converge, while reduce is told to keep what it was given.
    /// </remarks>
    public static SummarizationPrompt BuildCollapse(IReadOnlyList<string> partialSummaries) =>
        BuildNumberedParts(CollapseTemplate, partialSummaries);

    /// <summary>Builds the prompt that reduces several documents' summaries to one overview.</summary>
    /// <param name="summaries">The per-document summaries, labelled and in scope order.</param>
    /// <exception cref="ArgumentException"><paramref name="summaries"/> is null or whitespace.</exception>
    public static SummarizationPrompt BuildDigest(string summaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaries);

        var delimiter = CreateDelimiter();

        return new SummarizationPrompt(
            string.Format(CultureInfo.InvariantCulture, DigestTemplate, delimiter),
            Fence(delimiter, summaries));
    }

    #endregion
}
