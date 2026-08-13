using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Counts what a piece of text costs as context for a given deployment.
/// </summary>
/// <remarks>
/// Context cost, not billed cost: a pure function of the text, deterministic and recomputable.
/// What a turn was actually charged is only knowable from the provider and lives on the audit
/// trail instead.
/// </remarks>
public interface ITokenEstimator
{
    /// <summary>
    /// The accuracy class of the counts this estimator produces.
    /// </summary>
    TokenAccuracies Accuracy { get; }

    /// <summary>
    /// Counts the context cost of <paramref name="text"/>.
    /// </summary>
    /// <param name="deploymentName">The deployment the text will be sent to.</param>
    /// <param name="text">The text to count. Null or empty counts as zero.</param>
    /// <returns>The number of tokens the text contributes to a prompt.</returns>
    int CountTokens(string? deploymentName, string? text);
}

/// <summary>
/// Estimates context cost with a tiktoken vocabulary, corrected by a per-provider multiplier.
/// </summary>
/// <remarks>
/// tiktoken is OpenAI's own tokenizer, so it is exact for Azure OpenAI and an approximation
/// elsewhere. The multiplier is how that approximation is corrected from measured traffic; until
/// one is configured a provider is estimated at its raw count.
/// </remarks>
/// <param name="providerId">The provider this estimator serves, used to select its multiplier.</param>
/// <param name="tokenizerCache">Supplies the tokenizer for a deployment.</param>
/// <param name="options">The estimator's configured constants and multipliers.</param>
public sealed class TiktokenTokenEstimator(
    Guid providerId,
    TiktokenTokenizerCache tokenizerCache,
    IOptions<TokenEstimationOptions> options) : ITokenEstimator
{
    private readonly Guid _providerId = providerId;
    private readonly TiktokenTokenizerCache _tokenizerCache = tokenizerCache;
    private readonly IOptions<TokenEstimationOptions> _options = options;

    /// <inheritdoc />
    public TokenAccuracies Accuracy => TokenAccuracies.Estimated;

    /// <inheritdoc />
    public int CountTokens(string? deploymentName, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var settings = _options.Value;
        var tokenizer = _tokenizerCache.Get(deploymentName, settings.FallbackEncoding);
        var raw = tokenizer.CountTokens(text);

        if (!settings.CalibrationMultipliers.TryGetValue(_providerId, out var multiplier) || multiplier == 1.0)
        {
            return raw;
        }

        // Rounded up rather than to nearest: the estimate feeds a budget, and erring high leaves
        // headroom where erring low sends a prompt the model rejects.
        return (int)Math.Ceiling(raw * multiplier);
    }
}
