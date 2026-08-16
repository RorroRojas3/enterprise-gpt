namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Estimates what a piece of text costs as prompt tokens for a given deployment.
/// </summary>
/// <remarks>
/// The <em>context</em> cost — what a message contributes to the prompt on every future turn — and
/// deliberately not what a provider billed. It is a pure function of the text, so it is
/// deterministic, recomputable and comparable across roles and conversations.
/// </remarks>
public interface ITokenEstimator
{
    /// <summary>
    /// Estimates the tokens <paramref name="text"/> contributes.
    /// </summary>
    /// <param name="text">The text to measure.</param>
    /// <param name="deploymentName">
    /// The provider-side model or deployment identifier the text will be sent to, used to select a
    /// vocabulary. May be <see langword="null"/>.
    /// </param>
    /// <returns>The estimated token count, never negative.</returns>
    /// <remarks>
    /// Empty text returns zero without touching the tokenizer.
    /// </remarks>
    long CountTokens(string? text, string? deploymentName);
}
