using System.Collections.Frozen;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Connection settings for the Anthropic chat provider, bound from the <c>Anthropic</c> configuration
/// section and validated at startup when <see cref="Enabled"/> is set.
/// </summary>
/// <remarks>
/// Deliberately free of Anthropic SDK types so the service layer takes no Anthropic package
/// reference: <see cref="Thinking"/> and <see cref="Effort"/> are carried as strings and turned into
/// SDK types where the client is registered, the same split that keeps AWS types out of this project
/// for <see cref="AmazonBedrockOptions"/>.
/// </remarks>
public sealed class AnthropicOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Anthropic";

    /// <summary>
    /// The <see cref="Thinking"/> value that turns reasoning off.
    /// </summary>
    /// <remarks>
    /// A constant because three separate rules key on this one mode: the startup validator, the
    /// effort-pairing rule below, and the parser that turns it into an SDK type.
    /// </remarks>
    public const string DisabledThinking = "disabled";

    /// <summary>
    /// The values <see cref="Thinking"/> accepts, compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// Shared by the startup validator and its tests so neither can drift from the other. A fixed
    /// token budget is deliberately absent: it is rejected outright by every model this provider
    /// targets, and <see cref="Effort"/> is what replaced it.
    /// </remarks>
    public static readonly FrozenSet<string> ThinkingModes =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "adaptive", DisabledThinking);

    /// <summary>
    /// The values <see cref="Effort"/> accepts, compared case-insensitively.
    /// </summary>
    public static readonly FrozenSet<string> EffortLevels =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "low", "medium", "high", "xhigh", "max");

    /// <summary>
    /// Gets or sets a value indicating whether the Anthropic chat client is registered.
    /// Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The flag, rather than the presence of configuration, is what gates registration and
    /// validation, so an environment that does not use Anthropic needs no Anthropic settings at all
    /// and a partially filled-in section fails loudly instead of half-working.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Anthropic API key.
    /// </summary>
    /// <remarks>
    /// Keep it in user secrets locally and Key Vault when deployed, never in <c>appsettings.json</c>.
    /// Supplying it explicitly is also what suppresses the SDK's environment and profile credential
    /// resolution, so an <c>ANTHROPIC_API_KEY</c> that happens to be set on the host cannot
    /// authenticate a deployment whose configured key is wrong.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Anthropic model id used when a request does not carry one, for example
    /// <c>claude-opus-5</c>.
    /// </summary>
    /// <remarks>
    /// Every chat turn sets the model explicitly from the catalog, so this is only the fallback the
    /// SDK requires to construct a client. Ids are bare — no <c>anthropic.</c> prefix, which belongs
    /// to Bedrock, and no date suffix.
    /// </remarks>
    public string DefaultModelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the output token ceiling applied to a turn that does not carry one of its own.
    /// </summary>
    /// <remarks>
    /// In practice this is the ceiling on <em>every</em> Anthropic turn: <c>ConversationService</c>
    /// does not put the catalog model's <c>MaxOutputTokens</c> on <c>ChatOptions</c>, for any
    /// provider, so nothing overrides it today. Sized generously for that reason — with thinking on
    /// the ceiling covers reasoning and response text together, and a value fitted to the expected
    /// answer alone truncates the turn. Wiring the catalog value through is what would make this a
    /// fallback rather than the operative limit.
    /// </remarks>
    public int DefaultMaxOutputTokens { get; set; } = 32768;

    /// <summary>
    /// Gets or sets how the model reasons before answering: <c>adaptive</c>, which lets it decide per
    /// request, or <c>disabled</c>.
    /// </summary>
    public string Thinking { get; set; } = "adaptive";

    /// <summary>
    /// Gets or sets how much effort the model spends per turn: <c>low</c>, <c>medium</c>,
    /// <c>high</c>, <c>xhigh</c>, or <c>max</c>.
    /// </summary>
    /// <remarks>
    /// Applies to every Anthropic-backed model in the catalog, because a model row carries no
    /// per-model override. Raising it buys depth at the cost of latency and tokens.
    /// </remarks>
    public string Effort { get; set; } = "high";

    /// <summary>
    /// Gets or sets how long a single request may run before the SDK abandons it.
    /// </summary>
    /// <remarks>
    /// The SDK's own default. Turns that combine thinking with several tool-invocation rounds run
    /// long, so trimming this is a good way to manufacture timeouts on exactly the requests that
    /// most needed to finish.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets how many times the SDK retries a failed request.
    /// </summary>
    /// <remarks>
    /// Covers connection errors and 408, 409, 429, and 5xx responses with exponential backoff. Worst
    /// case wall-clock is <see cref="Timeout"/> multiplied by one more than this value.
    /// </remarks>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Gets a value indicating whether <see cref="Thinking"/> and <see cref="Effort"/> can be sent
    /// together.
    /// </summary>
    /// <remarks>
    /// Disabled thinking is accepted only at <c>high</c> effort or below; pairing it with
    /// <c>xhigh</c> or <c>max</c> is rejected by the model with a 400 on every request. Checked at
    /// startup so the combination cannot reach a conversation.
    /// </remarks>
    public bool IsThinkingEffortCombinationValid =>
        !string.Equals(Thinking, DisabledThinking, StringComparison.OrdinalIgnoreCase)
        || !(string.Equals(Effort, "xhigh", StringComparison.OrdinalIgnoreCase)
             || string.Equals(Effort, "max", StringComparison.OrdinalIgnoreCase));
}
