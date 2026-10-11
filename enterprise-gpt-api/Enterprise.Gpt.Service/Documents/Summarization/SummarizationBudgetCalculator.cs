using Enterprise.Gpt.Service.Documents.Summarization.Exceptions;
using Enterprise.Gpt.Service.Documents.Summarization.Models;

namespace Enterprise.Gpt.Service.Documents.Summarization;

/// <summary>
/// Decides how much of the summarizer's context window one summarization call may use.
/// </summary>
/// <remarks>
/// Pure, so the fit decision, the one most likely to go quietly wrong, is testable without a running job.
/// It decides what is sent, never what is summarized: an oversized document is split, not truncated.
/// </remarks>
public static class SummarizationBudgetCalculator
{
    #region Public static methods

    /// <summary>
    /// Computes the token budget for one summarization stage.
    /// </summary>
    /// <param name="summarizer">The catalog row as it was read.</param>
    /// <param name="promptOverheadTokens">This stage's instruction framing, already counted.</param>
    /// <param name="safetyFraction">The fraction of the raw budget a call may occupy.</param>
    /// <param name="mapUnitFraction">The fraction of the per-call budget one map unit may occupy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="summarizer"/> is <see langword="null"/>.</exception>
    /// <exception cref="SummarizerNotConfiguredException">
    /// The catalog row declares no usable context window.
    /// </exception>
    /// <remarks>
    /// A non-positive <c>ContextWindowSize</c> is refused, not read as unbounded as in
    /// <see cref="Conversations.Tokenization.ContextBudgetCalculator.Apply"/>: zero is the seeded
    /// default, and unbounded would send a huge document in one call.
    /// </remarks>
    public static SummarizationBudget Compute(
        SummarizerModel summarizer,
        long promptOverheadTokens,
        double safetyFraction,
        double mapUnitFraction)
    {
        ArgumentNullException.ThrowIfNull(summarizer);

        if (summarizer.ContextWindowSize <= 0)
        {
            throw new SummarizerNotConfiguredException(
                summarizer.ModelId,
                $"The summarizer's catalog row '{summarizer.ModelId}' declares a context window of "
                    + $"{summarizer.ContextWindowSize}. Set ContextWindowSize on that model before "
                    + "requesting a summary.");
        }

        var raw = (long)decimal.Truncate(summarizer.ContextWindowSize - summarizer.MaxOutputTokens)
            - Math.Max(0, promptOverheadTokens);

        if (raw <= 0)
        {
            throw new SummarizerNotConfiguredException(
                summarizer.ModelId,
                $"The summarizer's catalog row '{summarizer.ModelId}' leaves no room for a prompt: a "
                    + $"context window of {summarizer.ContextWindowSize} cannot hold an output "
                    + $"reservation of {summarizer.MaxOutputTokens} plus {promptOverheadTokens} "
                    + "tokens of framing.");
        }

        var perCall = Math.Max(1, (long)(raw * safetyFraction));
        var mapUnit = Math.Max(1, (long)(perCall * mapUnitFraction));

        return new SummarizationBudget(perCall, mapUnit);
    }

    #endregion
}
