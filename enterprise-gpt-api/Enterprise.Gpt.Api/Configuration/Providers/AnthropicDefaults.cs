using Anthropic.Models.Messages;
using Enterprise.Gpt.Api.Options;
using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Stamps Anthropic-native request settings onto every turn served by the Anthropic chat client.
/// </summary>
/// <remarks>
/// A file of its own so <c>Anthropic.Models.Messages</c>, whose <c>Model</c> collides with
/// <see cref="Enterprise.Gpt.Entity.ChatModels.Model"/>, stays out of the provider-agnostic code.
/// </remarks>
internal static class AnthropicDefaults
{
    #region Private methods

    private static ThinkingConfigParam ParseThinking(string thinking) =>
        thinking.ToLowerInvariant() switch
        {
            "adaptive" => new ThinkingConfigAdaptive(),
            AnthropicOptions.DisabledThinking => new ThinkingConfigDisabled(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(thinking), thinking, "Unknown Anthropic thinking mode.")
        };

    private static Effort ParseEffort(string effort) =>
        effort.ToLowerInvariant() switch
        {
            "low" => Effort.Low,
            "medium" => Effort.Medium,
            "high" => Effort.High,
            "xhigh" => Effort.Xhigh,
            "max" => Effort.Max,
            _ => throw new ArgumentOutOfRangeException(
                nameof(effort), effort, "Unknown Anthropic effort level.")
        };

    #endregion

    #region Public static methods

    /// <summary>Creates the <see cref="ChatOptions"/> callback for a configured Anthropic provider.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="settings"/> carries a thinking mode or effort level the parsers do not know.
    /// </exception>
    /// <remarks>
    /// The keyed client is built lazily, so a parse failure would surface on the first conversation, not
    /// at startup; <c>AnthropicDefaultsTests</c> pins every value the validators accept.
    /// </remarks>
    public static Action<ChatOptions> Create(AnthropicOptions settings)
    {
        var thinking = ParseThinking(settings.Thinking);
        var effort = ParseEffort(settings.Effort);
        var defaultModelId = settings.DefaultModelId;
        var defaultMaxOutputTokens = settings.DefaultMaxOutputTokens;

        return options =>
        {
            // temperature, top_p and top_k are a 400 on the models this provider targets (Opus 4.7+, Sonnet 5,
            // Fable 5); cleared so a caller setting them for the other providers does not break only this one.
            options.Temperature = null;
            options.TopP = null;
            options.TopK = null;

            // Assigned only when the caller has not supplied one, so a call site that needs different
            // parameters for a single turn keeps the escape hatch Microsoft.Extensions.AI provides.
            options.RawRepresentationFactory ??= _ => new MessageCreateParams
            {
                // The bridge does not overlay Model or MaxTokens, so these reach the wire; reading them off
                // ChatOptions keeps per-model routing working.
                Model = string.IsNullOrWhiteSpace(options.ModelId) ? defaultModelId : options.ModelId,
                MaxTokens = options.MaxOutputTokens ?? defaultMaxOutputTokens,
                // Empty, not a placeholder: the bridge concatenates this list ahead of the real
                // conversation, so anything here would be prepended to every turn.
                Messages = [],
                Thinking = thinking,
                OutputConfig = new OutputConfig { Effort = effort }
            };
        };
    }

    #endregion
}
