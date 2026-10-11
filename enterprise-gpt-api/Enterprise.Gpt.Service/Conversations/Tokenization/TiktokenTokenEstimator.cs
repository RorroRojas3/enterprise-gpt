using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Conversations.Tokenization;

/// <summary>
/// Estimates what a piece of text costs as prompt tokens for a given deployment.
/// </summary>
/// <remarks>
/// The <em>context</em> cost, deliberately not what a provider billed: a pure function of the text, so it
/// is deterministic and comparable across roles and conversations.
/// </remarks>
public interface ITokenEstimator
{
    /// <summary>
    /// Estimates the tokens <paramref name="text"/> contributes, never negative; empty text returns zero
    /// without touching the tokenizer. <paramref name="deploymentName"/> selects the vocabulary.
    /// </summary>
    long CountTokens(string? text, string? deploymentName);
}

/// <summary>
/// Counts tokens with a local tiktoken vocabulary, corrected by the calling provider's configured
/// calibration multiplier.
/// </summary>
/// <remarks>
/// One instance per provider; the multiplier adapts OpenAI's tiktoken to other providers' tokenizers. The
/// vocabularies are shared through <see cref="TokenizerProvider"/>.
/// </remarks>
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

        // Rounded up: the budget this feeds is a ceiling, and rounding down is the direction that
        // overflows a context window.
        return _calibration is 1d ? raw : (long)Math.Ceiling(raw * _calibration);
    }
}
