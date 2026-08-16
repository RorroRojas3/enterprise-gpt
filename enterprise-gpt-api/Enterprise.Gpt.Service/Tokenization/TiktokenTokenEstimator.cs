using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Tokenization;

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
