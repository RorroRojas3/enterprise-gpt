using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Summarization;

/// <summary>
/// What one summarization stage is allowed to send, measured in tokens.
/// </summary>
/// <param name="PerCallTokens">
/// The most a single call's variable content — the document, or the partial summaries being
/// reduced — may occupy, after the output reservation, the framing and the safety fraction.
/// </param>
/// <param name="MapUnitTokens">
/// The most one map unit may occupy. Strictly below <paramref name="PerCallTokens"/>, because a
/// unit is sized before the map prompt's own framing is added to it.
/// </param>
public sealed record SummarizationBudget(long PerCallTokens, long MapUnitTokens);

/// <summary>
/// Decides how much of the summarizer's context window one summarization call may use.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately pure: no database, no chat client, no options object. The fit decision is the one
/// most likely to be wrong in a way nobody notices — a document quietly routed down the wrong path,
/// or a call sized past what the provider accepts — so it is the one that most needs to be testable
/// without a running job.
/// </para>
/// <para>
/// It decides what is <em>sent</em>, never what is <em>summarized</em>. A document too large for one
/// call is split, not truncated.
/// </para>
/// </remarks>
public static class SummarizationBudgetCalculator
{
    /// <summary>
    /// Computes the token budget for one summarization stage.
    /// </summary>
    /// <param name="summarizer">The summarizer's catalog row as it was read.</param>
    /// <param name="promptOverheadTokens">
    /// The prompt tokens belonging to no content — this stage's own instruction framing, already
    /// counted.
    /// </param>
    /// <param name="safetyFraction">The fraction of the raw budget a call may occupy.</param>
    /// <param name="mapUnitFraction">The fraction of the per-call budget one map unit may occupy.</param>
    /// <returns>What this stage may send.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="summarizer"/> is <see langword="null"/>.</exception>
    /// <exception cref="SummarizerNotConfiguredException">
    /// The catalog row declares no usable context window.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A <paramref name="summarizer"/> whose <c>ContextWindowSize</c> is zero or less is
    /// <em>unconfigured, and refused</em> — the opposite of what
    /// <see cref="Tokenization.ContextBudgetCalculator.Apply"/> means by the same value, where it
    /// reads as unbounded. That reading is right for a chat turn, whose alternative is trimming a
    /// user's conversation to nothing over an unset catalog field. It is wrong here: zero is the
    /// seeded default, and inheriting the unbounded convention would send a multi-million-token
    /// document in a single call — a provider rejection at best, an enormous bill at worst.
    /// </para>
    /// <para>
    /// The check runs per call rather than once at startup, because an administrator can edit the
    /// row's window back to zero while the application is already running.
    /// </para>
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
}
