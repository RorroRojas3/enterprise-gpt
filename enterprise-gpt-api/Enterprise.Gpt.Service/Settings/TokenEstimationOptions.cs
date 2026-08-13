namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Tuning knobs for the per-message context estimator, bound from the <c>TokenEstimation</c>
/// configuration section.
/// </summary>
public sealed class TokenEstimationOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "TokenEstimation";

    /// <summary>
    /// The encoding used when a deployment name is not a model identifier the tokenizer library
    /// recognizes. Default: <c>cl100k_base</c>.
    /// </summary>
    /// <remarks>
    /// Deployment names are operator-chosen aliases, so an unrecognized one is ordinary rather
    /// than exceptional. Counting with a near-enough vocabulary beats failing a turn.
    /// </remarks>
    public string FallbackEncoding { get; set; } = "cl100k_base";

    /// <summary>
    /// Prompt tokens attributed to each attached tool for the schema injected on its behalf.
    /// Default: 350.
    /// </summary>
    /// <remarks>
    /// Charged per tool rather than measured by serializing each schema: with several MCP servers
    /// attached this is easily thousands of tokens that belong to no message, and leaving it out
    /// makes the sum of the messages permanently under-report the prompt. A configured constant is
    /// less accurate than counting the real schemas and far cheaper; the reconciliation metric is
    /// what says whether the difference matters.
    /// </remarks>
    public int PerToolSchemaTokens { get; set; } = 350;

    /// <summary>
    /// Prompt tokens attributed to each message for the chat framing around it. Default: 4.
    /// </summary>
    public int PerMessageFramingTokens { get; set; } = 4;

    /// <summary>
    /// Per-provider correction applied to the raw token count, keyed by provider identifier.
    /// A provider with no entry is estimated at 1.0.
    /// </summary>
    /// <remarks>
    /// tiktoken is OpenAI's tokenizer, and it undercounts other providers — by roughly 15–20% on
    /// Claude prose and more on code. This is the seam for correcting that from measured traffic
    /// rather than by guessing, which is why it is configuration rather than a constant.
    /// </remarks>
    public Dictionary<Guid, double> CalibrationMultipliers { get; set; } = [];

    /// <summary>
    /// The largest calibration multiplier accepted at startup. Default: 3.0.
    /// </summary>
    /// <remarks>
    /// A ceiling exists so a mistyped multiplier fails at boot rather than silently tripling every
    /// budget and every stored count.
    /// </remarks>
    public double MaxCalibrationMultiplier { get; set; } = 3.0;
}
