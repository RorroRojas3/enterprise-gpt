using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Conversations.Tokenization;

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

/// <summary>
/// Counts tokens with a local tiktoken vocabulary, corrected by the calling provider's configured
/// calibration multiplier.
/// </summary>
/// <remarks>
/// One instance per provider, each holding only its own multiplier; the vocabularies themselves are
/// shared through <see cref="TokenizerProvider"/>. tiktoken is OpenAI's tokenizer, so the multiplier
/// is what makes the same counting path usable for providers whose tokenizer it is not.
/// </remarks>
/// <param name="providerId">The provider this estimator counts for.</param>
/// <param name="tokenizers">The shared vocabulary cache.</param>
/// <param name="options">The estimator settings, read for the calibration multiplier.</param>
public sealed class TiktokenTokenEstimator(
    Guid providerId,
    TokenizerProvider tokenizers,
    IOptions<TokenEstimationOptions> options) : ITokenEstimator
{
    private readonly TokenizerProvider _tokenizers = tokenizers;
    private readonly double _calibration = options.Value.CalibrationFor(providerId);

    /// <inheritdoc />
    public long CountTokens(string? text, string? deploymentName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var raw = _tokenizers.Get(deploymentName).CountTokens(text);

        // Rounded up so a calibrated estimate never lands below the raw count for a provider whose
        // tokens are known to be denser than tiktoken's: the budget this feeds is a ceiling, and
        // rounding down is the direction that overflows a context window.
        return _calibration is 1d ? raw : (long)Math.Ceiling(raw * _calibration);
    }
}
