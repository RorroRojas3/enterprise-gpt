using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <remarks>
/// Deliberately a local approximation: a provider round trip would add a call per message to every turn. Its
/// error is measured on tool-free turns and corrected per provider by <see cref="ProviderCalibration"/>.
/// </remarks>
public sealed class TokenEstimationOptions
{
    public const string SectionName = "Tokenization";

    /// <summary>
    /// Encoding used when the tokenizer library does not recognize a deployment name, as is usual on Azure.
    /// </summary>
    public string FallbackEncoding { get; set; } = "o200k_base";

    /// <summary>
    /// Role markers and delimiters each message is charged for; four is the long-standing OpenAI figure.
    /// </summary>
    public int PerMessageFramingTokens { get; set; } = 4;

    /// <summary>
    /// A flat cost per attached tool schema, which belongs to no message; counting each schema exactly is not
    /// worth it until the reconciliation metric says otherwise.
    /// </summary>
    public int PerToolSchemaTokens { get; set; } = 120;

    /// <summary>A value far above one is a mistake that silently shrinks every replayed history.</summary>
    public double MaxCalibrationMultiplier { get; set; } = 3;

    /// <summary>
    /// Multipliers keyed by provider id, correcting tiktoken's measured undercount of other vendors (Claude
    /// by roughly 15-20% on prose) without a deployment. A missing entry means 1.0.
    /// </summary>
    public Dictionary<string, double> ProviderCalibration { get; } = [];

    /// <summary>The provider's multiplier, or 1 when it has no entry.</summary>
    public double CalibrationFor(Guid providerId)
    {
        return ProviderCalibration.TryGetValue(providerId.ToString(), out var multiplier) ? multiplier : 1d;
    }
}

public sealed class TokenEstimationOptionsValidator : AbstractValidator<TokenEstimationOptions>
{
    public TokenEstimationOptionsValidator()
    {
        RuleFor(options => options.FallbackEncoding).NotEmpty().WithMessage("Tokenization:FallbackEncoding is required.");
        RuleFor(options => options.PerMessageFramingTokens).InclusiveBetween(0, 100)
            .WithMessage("Tokenization:PerMessageFramingTokens must be between 0 and 100.");
        RuleFor(options => options.PerToolSchemaTokens).InclusiveBetween(0, 10000)
            .WithMessage("Tokenization:PerToolSchemaTokens must be between 0 and 10000.");
        RuleFor(options => options.MaxCalibrationMultiplier).InclusiveBetween(1d, 100d)
            .WithMessage("Tokenization:MaxCalibrationMultiplier must be between 1 and 100.");
        RuleFor(options => options.ProviderCalibration)
            .Must((options, calibration) => calibration.Values.All(multiplier => multiplier > 0 && multiplier <= options.MaxCalibrationMultiplier))
            .WithMessage("Every Tokenization:ProviderCalibration value must be greater than zero and at most Tokenization:MaxCalibrationMultiplier.");
        RuleFor(options => options.ProviderCalibration)
            .Must(calibration => calibration.Keys.All(key => Guid.TryParse(key, out _)))
            .WithMessage("Every Tokenization:ProviderCalibration key must be a provider id in GUID form.");
    }
}
