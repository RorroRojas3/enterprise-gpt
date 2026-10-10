using Anthropic.Models.Messages;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Chat;

/// <summary>
/// Builds the callback that stamps Anthropic-native request settings onto every turn served by the
/// Anthropic chat client.
/// </summary>
/// <remarks>
/// Kept out of <c>Program.cs</c> for two reasons: it is the only place that needs
/// <c>Anthropic.Models.Messages</c>, whose <c>Model</c> type would otherwise collide with
/// <see cref="Enterprise.Gpt.Entity.Model"/>, and confining the SDK's request types to one file keeps
/// them out of the provider-agnostic <c>ChatOptions</c> that <c>ConversationService</c> builds.
/// </remarks>
internal static class AnthropicChatDefaults
{
    /// <summary>
    /// Creates the <see cref="ChatOptions"/> callback for a configured Anthropic provider.
    /// </summary>
    /// <param name="settings">The validated Anthropic settings.</param>
    /// <returns>A callback suitable for <c>ChatClientBuilder.ConfigureOptions</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="settings"/> carries a thinking mode or effort level the parsers do not know.
    /// The startup validators reject those first, and <c>AnthropicChatDefaultsTests</c> pins every
    /// value they accept — the two vocabularies drifting apart is what this guards.
    /// </exception>
    /// <remarks>
    /// Both values are parsed once here rather than per request. Note that the keyed chat client is
    /// built lazily on first resolution, so a parse failure would surface on the first conversation
    /// rather than at startup; the tests, not the validators, are what keep that unreachable.
    /// </remarks>
    public static Action<ChatOptions> Create(AnthropicOptions settings)
    {
        var thinking = ParseThinking(settings.Thinking);
        var effort = ParseEffort(settings.Effort);
        var defaultModelId = settings.DefaultModelId;
        var defaultMaxOutputTokens = settings.DefaultMaxOutputTokens;

        return options =>
        {
            // temperature, top_p, and top_k were removed from the request surface on the models this
            // provider targets — Opus 4.7 and later, Sonnet 5, Fable 5 — where sending one is a 400 on
            // every turn regardless of the thinking mode. Clearing them here means a caller that sets
            // them for the other providers does not silently break only this one.
            options.Temperature = null;
            options.TopP = null;
            options.TopK = null;

            // Assigned only when the caller has not supplied one, so a call site that needs different
            // parameters for a single turn keeps the escape hatch Microsoft.Extensions.AI provides.
            options.RawRepresentationFactory ??= _ => new MessageCreateParams
            {
                // The bridge merges the messages, tools, and system content it derives from
                // ChatOptions onto this object, but it does *not* overlay Model or MaxTokens — the
                // values set here are what reach the wire. Reading them back off ChatOptions is what
                // keeps per-model routing working; hardcoding either would pin every Anthropic
                // conversation to one model.
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
}
