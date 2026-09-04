using System.Globalization;
using System.Text;

namespace Enterprise.Gpt.Service.Prompts;

/// <summary>
/// One summarization call's prompt, split across the two places it is carried.
/// </summary>
/// <param name="Instructions">
/// The framing, for <c>ChatOptions.Instructions</c>. Constant per stage, so its token cost can be
/// counted once and charged to the budget as overhead.
/// </param>
/// <param name="UserMessage">
/// The delimited content, for the single user message. This is what the budget measures.
/// </param>
public sealed record SummarizationPrompt(string Instructions, string UserMessage);

/// <summary>
/// Loads and parameterises the document summarization prompts from markdown templates shipped
/// alongside the assembly under the <c>Prompts/</c> folder.
/// </summary>
/// <remarks>
/// <para>
/// The framing rides <c>ChatOptions.Instructions</c> and the document text rides one user message,
/// rather than both being interpolated into the instructions the way project instructions are. The
/// budget arithmetic requires the split: the fit decision counts the framing as overhead and
/// measures the document against what is left, which only balances if the document is the message
/// being measured.
/// </para>
/// <para>
/// No document name reaches a prompt's framing. A file name is attacker-chosen text, and a project
/// document is readable by every member of its project, so a name carried into the instructions
/// would be a cross-user injection channel <em>inside</em> the frame, where the trust paragraph —
/// which speaks only about the delimited body — would not cover it. The digest labels its documents
/// by name, and those labels ride inside the fence for that reason.
/// </para>
/// <para>
/// Templates are read once at type initialisation. If a template file is missing, the type
/// initialiser throws <see cref="FileNotFoundException"/>, surfaced to callers as
/// <see cref="TypeInitializationException"/>.
/// </para>
/// </remarks>
public static class SummarizationPrompts
{
    /// <summary>
    /// Identifies the prompt generation a summary was produced under.
    /// </summary>
    /// <remarks>
    /// Persisted on a summary row so that a summary generated before a template changed can be told
    /// apart from one generated after, without comparing the text itself. Bump this whenever any of
    /// the templates changes in a way that would change what a model returns.
    /// </remarks>
    public const string PromptVersion = "2";

    private static readonly string SinglePassTemplate =
        PromptTemplateLoader.Load("document-summary-single-pass-prompt.md");

    private static readonly string MapTemplate =
        PromptTemplateLoader.Load("document-summary-map-prompt.md");

    private static readonly string ReduceTemplate =
        PromptTemplateLoader.Load("document-summary-reduce-prompt.md");

    private static readonly string CollapseTemplate =
        PromptTemplateLoader.Load("document-summary-collapse-prompt.md");

    private static readonly string DigestTemplate =
        PromptTemplateLoader.Load("document-summary-digest-prompt.md");

    #region Public static methods

    /// <summary>
    /// Builds the prompt for a document that fits the summarizer's budget in one call.
    /// </summary>
    /// <param name="documentText">The document's reassembled text.</param>
    /// <returns>The framing and the delimited document.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="documentText"/> is <see langword="null"/> or whitespace.</exception>
    public static SummarizationPrompt BuildSinglePass(string documentText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentText);

        var delimiter = CreateDelimiter();

        return new SummarizationPrompt(
            string.Format(CultureInfo.InvariantCulture, SinglePassTemplate, delimiter),
            Fence(delimiter, documentText));
    }

    /// <summary>
    /// Builds the prompt for one map unit of an oversized document.
    /// </summary>
    /// <param name="unitNumber">This unit's one-based position.</param>
    /// <param name="unitCount">How many units the document was split into.</param>
    /// <param name="unitText">This unit's text.</param>
    /// <returns>The framing and the delimited unit.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="unitText"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="unitNumber"/> is not between one and <paramref name="unitCount"/>.
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

    /// <summary>
    /// Builds the prompt that assembles part summaries into the document's final summary.
    /// </summary>
    /// <param name="partialSummaries">The part summaries, in document order.</param>
    /// <returns>The framing and the delimited, numbered parts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="partialSummaries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="partialSummaries"/> is empty.</exception>
    public static SummarizationPrompt BuildReduce(IReadOnlyList<string> partialSummaries) =>
        BuildNumberedParts(ReduceTemplate, partialSummaries);

    /// <summary>
    /// Builds the prompt that shortens part summaries until they fit one reduce call.
    /// </summary>
    /// <param name="partialSummaries">The part summaries to compress, in document order.</param>
    /// <returns>The framing and the delimited, numbered parts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="partialSummaries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="partialSummaries"/> is empty.</exception>
    /// <remarks>
    /// Separate from <see cref="BuildReduce"/> because the two stages want opposite things: a
    /// collapse pass has to come back shorter or the loop never converges, while the reduce it feeds
    /// produces the summary that gets cached and is told to keep what it was given.
    /// </remarks>
    public static SummarizationPrompt BuildCollapse(IReadOnlyList<string> partialSummaries) =>
        BuildNumberedParts(CollapseTemplate, partialSummaries);

    /// <summary>
    /// Builds the prompt that reduces several documents' summaries to one overview.
    /// </summary>
    /// <param name="summaries">The per-document summaries, labelled and in scope order.</param>
    /// <returns>The framing and the delimited summaries.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="summaries"/> is <see langword="null"/> or whitespace.</exception>
    public static SummarizationPrompt BuildDigest(string summaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaries);

        var delimiter = CreateDelimiter();

        return new SummarizationPrompt(
            string.Format(CultureInfo.InvariantCulture, DigestTemplate, delimiter),
            Fence(delimiter, summaries));
    }

    #endregion

    #region Private methods

    /// <remarks>
    /// The parts are numbered inside one fenced block rather than fenced individually, so the model
    /// sees a single trust boundary it was told about rather than a sequence of them.
    /// </remarks>
    private static SummarizationPrompt BuildNumberedParts(
        string template,
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

    /// <remarks>
    /// Unguessable per call rather than a fixed string, for the reason
    /// <c>ConversationPrompts.BuildProjectInstructionsPrompt</c> gives: a fixed delimiter can be
    /// reproduced inside the untrusted body, which lets the text appear to close its own block and
    /// continue as if it were part of the frame.
    /// </remarks>
    private static string CreateDelimiter() => $"<<<document-text:{Guid.NewGuid():N}>>>";

    private static string Fence(string delimiter, string content) =>
        $"{delimiter}\n{content}\n{delimiter}";

    #endregion
}
