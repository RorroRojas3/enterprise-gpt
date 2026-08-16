using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Tuning knobs for the per-message token estimator, bound from the <c>Tokenization</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// The estimate is deliberately a local, cheap approximation rather than a provider round trip:
/// counting tokens over the network on the write path would add a call per message to every turn.
/// The cost of that choice is error, which is measured against provider-reported usage on turns
/// with no tool calls and corrected per provider by <see cref="ProviderCalibration"/>.
/// </remarks>
public sealed class TokenEstimationOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Tokenization";

    /// <summary>
    /// Gets or sets the encoding used when a deployment name is not a model the tokenizer library
    /// recognizes. Default: <c>o200k_base</c>.
    /// </summary>
    /// <remarks>
    /// Azure OpenAI addresses models by deployment name, chosen by whoever created the deployment
    /// and usually not a model identifier. The conservative default is the older encoding, which
    /// undercounts a modern <c>o200k_base</c> family slightly; a deployment that knows its models
    /// are modern should set this to <c>o200k_base</c> and will get a closer estimate.
    /// </remarks>
    public string FallbackEncoding { get; set; } = "o200k_base";

    /// <summary>
    /// Gets or sets the tokens added per message to account for the chat format's own framing.
    /// Default: 4.
    /// </summary>
    /// <remarks>
    /// Every message costs more than its text: the wire format adds role markers and delimiters
    /// that belong to no message's content but are charged to the prompt. Four is the long-standing
    /// figure for OpenAI's chat format and is configurable because it is a property of the
    /// provider's format, not of this application.
    /// </remarks>
    [Range(0, 100)]
    public int PerMessageFramingTokens { get; set; } = 4;

    /// <summary>
    /// Gets or sets the tokens attributed to each tool's JSON schema injected into a turn's prompt.
    /// Default: 120.
    /// </summary>
    /// <remarks>
    /// Modelled as a flat cost per attached tool rather than by serializing each schema and
    /// counting it. With several MCP servers attached this is easily thousands of tokens that
    /// belong to no message, so omitting the term would make the sum of the messages always
    /// under-report the prompt; a schema-accurate count is more work than the accuracy is worth
    /// until the reconciliation metric says otherwise.
    /// </remarks>
    [Range(0, 10000)]
    public int PerToolSchemaTokens { get; set; } = 120;

    /// <summary>
    /// Gets or sets the largest calibration multiplier accepted. Default: 3.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than an open range: a multiplier is a correction for a known undercount,
    /// and a value far above one is a configuration mistake that would silently shrink every
    /// conversation's replayed history.
    /// </remarks>
    [Range(1, 100)]
    public double MaxCalibrationMultiplier { get; set; } = 3;

    /// <summary>
    /// Gets the per-provider multipliers applied to the raw count, keyed by provider identifier.
    /// </summary>
    /// <remarks>
    /// tiktoken is OpenAI's tokenizer and undercounts other vendors — Claude by roughly 15–20% on
    /// prose and by more on code and non-English text. This is the seam that corrects a measured
    /// undercount without a code deployment. A provider with no entry uses 1.0, which is the right
    /// default for the provider whose tokenizer this actually is.
    /// </remarks>
    public Dictionary<string, double> ProviderCalibration { get; } = [];

    /// <summary>
    /// Resolves the multiplier configured for a provider.
    /// </summary>
    /// <param name="providerId">The provider serving the model.</param>
    /// <returns>The configured multiplier, or 1 when the provider has no entry.</returns>
    public double CalibrationFor(Guid providerId)
    {
        return ProviderCalibration.TryGetValue(providerId.ToString(), out var multiplier) ? multiplier : 1d;
    }
}
