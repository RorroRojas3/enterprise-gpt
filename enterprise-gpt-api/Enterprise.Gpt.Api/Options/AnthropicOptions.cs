using System.Collections.Frozen;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>
/// Anthropic chat provider settings, validated at startup when <see cref="Enabled"/> is set.
/// </summary>
/// <remarks>
/// Free of Anthropic SDK types so the service layer takes no Anthropic package reference;
/// <see cref="Thinking"/> and <see cref="Effort"/> become SDK types where the client is registered.
/// </remarks>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>The <see cref="Thinking"/> value that turns reasoning off.</summary>
    public const string DisabledThinking = "disabled";

    /// <summary>The values <see cref="Thinking"/> accepts, compared case-insensitively.</summary>
    /// <remarks>
    /// No fixed token budget: every targeted model rejects it, and <see cref="Effort"/> replaced it.
    /// </remarks>
    public static readonly FrozenSet<string> ThinkingModes =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "adaptive", DisabledThinking);

    /// <summary>The values <see cref="Effort"/> accepts, compared case-insensitively.</summary>
    public static readonly FrozenSet<string> EffortLevels =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "low", "medium", "high", "xhigh", "max");

    /// <summary>Whether the Anthropic chat client is registered.</summary>
    /// <remarks>
    /// The flag, not the section's presence, gates registration and validation, so a partially
    /// filled-in section fails loudly instead of half-working.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>Anthropic API key; keep it in user secrets or Key Vault.</summary>
    /// <remarks>
    /// Supplying it explicitly suppresses the SDK's environment credential lookup, so a stray
    /// <c>ANTHROPIC_API_KEY</c> on the host cannot mask a wrong configured key.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Fallback model id the SDK requires at construction, for example <c>claude-opus-5</c>.
    /// </summary>
    /// <remarks>
    /// Every turn sets the model from the catalog. Ids are bare: no Bedrock <c>anthropic.</c> prefix,
    /// no date suffix.
    /// </remarks>
    public string DefaultModelId { get; set; } = string.Empty;

    /// <summary>Output token ceiling for a turn that does not carry its own.</summary>
    /// <remarks>
    /// In practice it caps every Anthropic turn, since <c>ConversationService</c> does not pass the
    /// catalog <c>MaxOutputTokens</c>; sized generously because with thinking on it covers reasoning too.
    /// </remarks>
    public int DefaultMaxOutputTokens { get; set; } = 32768;

    /// <summary>
    /// How the model reasons before answering: <c>adaptive</c> (it decides per request) or
    /// <c>disabled</c>.
    /// </summary>
    public string Thinking { get; set; } = "adaptive";

    /// <summary>
    /// Effort per turn: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, or <c>max</c>.
    /// </summary>
    /// <remarks>Applies to every Anthropic-backed model; a model row carries no per-model override.</remarks>
    public string Effort { get; set; } = "high";

    /// <summary>How long a single request may run before the SDK abandons it.</summary>
    /// <remarks>
    /// The SDK default. Thinking turns with several tool rounds run long, so trimming this times out
    /// exactly the requests that most need to finish.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many times the SDK retries a failed request (connection errors, 408, 409, 429, 5xx).
    /// </summary>
    /// <remarks>Worst-case wall-clock is <see cref="Timeout"/> times one more than this value.</remarks>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Whether <see cref="Thinking"/> and <see cref="Effort"/> can be sent together.</summary>
    /// <remarks>
    /// The model rejects disabled thinking above <c>high</c> effort with a 400 on every request, so
    /// startup validation refuses the pair.
    /// </remarks>
    public bool IsThinkingEffortCombinationValid =>
        !string.Equals(Thinking, DisabledThinking, StringComparison.OrdinalIgnoreCase)
        || !(string.Equals(Effort, "xhigh", StringComparison.OrdinalIgnoreCase)
             || string.Equals(Effort, "max", StringComparison.OrdinalIgnoreCase));
}

internal sealed class AnthropicOptionsValidator : AbstractValidator<AnthropicOptions>
{
    public AnthropicOptionsValidator()
    {
        When(options => options.Enabled, () =>
        {
            RuleFor(options => options.ApiKey).NotEmpty()
                .WithMessage("Anthropic:ApiKey is required when Anthropic:Enabled is true.");
            RuleFor(options => options.DefaultModelId).NotEmpty()
                .WithMessage("Anthropic:DefaultModelId is required when Anthropic:Enabled is true.");
            // The Bedrock section takes 'anthropic.'-prefixed ids, and copying one across is the mistake it invites.
            RuleFor(options => options.DefaultModelId)
                .Must(id => !id.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
                .WithMessage("Anthropic:DefaultModelId must be a bare Anthropic model id; the 'anthropic.' prefix belongs to Bedrock.");
            RuleFor(options => options.DefaultMaxOutputTokens).GreaterThan(0)
                .WithMessage("Anthropic:DefaultMaxOutputTokens must be greater than zero.");
            RuleFor(options => options.Timeout).GreaterThan(TimeSpan.Zero)
                .WithMessage("Anthropic:Timeout must be greater than zero.");
            RuleFor(options => options.MaxRetries).GreaterThanOrEqualTo(0)
                .WithMessage("Anthropic:MaxRetries cannot be negative.");
            // Null-guarded before the lookup: the case-insensitive comparer throws on a null rather than reporting a miss.
            RuleFor(options => options.Thinking)
                .Must(value => !string.IsNullOrWhiteSpace(value) && AnthropicOptions.ThinkingModes.Contains(value))
                .WithMessage("Anthropic:Thinking must be 'adaptive' or 'disabled'.");
            RuleFor(options => options.Effort)
                .Must(value => !string.IsNullOrWhiteSpace(value) && AnthropicOptions.EffortLevels.Contains(value))
                .WithMessage("Anthropic:Effort must be one of low, medium, high, xhigh, max.");
            // Above 'high' effort the model rejects every request with disabled thinking as a 400.
            RuleFor(options => options.Thinking).Must((options, _) => options.IsThinkingEffortCombinationValid)
                .WithMessage("Anthropic:Thinking 'disabled' is not accepted with Anthropic:Effort 'xhigh' or 'max'.");
        });
    }
}
