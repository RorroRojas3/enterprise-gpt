using System.Collections.Frozen;
using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <summary>Settings for the Azure OpenAI chat and embedding clients.</summary>
/// <remarks>
/// The Responses-API provider, the only one that can serve reasoning models. No <c>Enabled</c> flag: it
/// serves the default model and owns the embedding generator, so missing settings must fail startup.
/// </remarks>
public sealed class AzureOpenAIOptions : AzureV1EndpointOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>
    /// Accepted <see cref="ReasoningSummary"/> values. <c>none</c> is this app's own, turning summaries off
    /// globally without editing every model row.
    /// </summary>
    public static readonly FrozenSet<string> ReasoningSummaries =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "auto", "concise", "detailed", "none");

    public static readonly FrozenSet<string> ReasoningEfforts =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "minimal", "low", "medium", "high");

    /// <summary>
    /// The embedding deployment. It must return 1536-dimension vectors (the stored column width) and selects
    /// the chunker's tokenizer, so changing it invalidates every stored embedding.
    /// </summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Reasoning summary verbosity, or <c>none</c>. Sent only to models with <c>IsReasoningEnabled</c>, since
    /// other deployments reject reasoning options outright.
    /// </summary>
    public string ReasoningSummary { get; set; } = "auto";

    /// <summary>
    /// Reasoning effort for every reasoning-enabled model; there is no per-model override. Reasoning tokens
    /// bill as output.
    /// </summary>
    public string ReasoningEffort { get; set; } = "medium";

    public bool IsReasoningSummaryRequested =>
        !string.Equals(ReasoningSummary, "none", StringComparison.OrdinalIgnoreCase);
}

public sealed class AzureOpenAIOptionsValidator : AbstractValidator<AzureOpenAIOptions>
{
    public AzureOpenAIOptionsValidator()
    {
        RuleFor(options => options.Url).Must((options, _) => options.IsUrlAbsolute)
            .WithMessage("AzureOpenAI:Url must be an absolute http or https URI, for example https://my-resource.services.ai.azure.com/.");
        // The client appends the v1 path, so configuring it here doubles it into an unexplained 404.
        RuleFor(options => options.Url).Must((options, _) => options.IsUrlResourceRoot)
            .WithMessage("AzureOpenAI:Url must be the resource root, without /openai/v1 — the client appends it.");
        RuleFor(options => options.ApiKey).NotEmpty().WithMessage("AzureOpenAI:ApiKey is required.");
        RuleFor(options => options.DefaultModel).NotEmpty().WithMessage("AzureOpenAI:DefaultModel is required.");
        RuleFor(options => options.EmbeddingModel).NotEmpty().WithMessage("AzureOpenAI:EmbeddingModel is required.");
        // Null-guarded before the lookup: the case-insensitive comparer throws on a null rather than reporting a miss.
        RuleFor(options => options.ReasoningSummary)
            .Must(value => !string.IsNullOrWhiteSpace(value) && AzureOpenAIOptions.ReasoningSummaries.Contains(value))
            .WithMessage("AzureOpenAI:ReasoningSummary must be one of auto, concise, detailed, none.");
        RuleFor(options => options.ReasoningEffort)
            .Must(value => !string.IsNullOrWhiteSpace(value) && AzureOpenAIOptions.ReasoningEfforts.Contains(value))
            .WithMessage("AzureOpenAI:ReasoningEffort must be one of minimal, low, medium, high.");
    }
}
