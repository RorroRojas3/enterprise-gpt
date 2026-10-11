using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

// OPENAI001: Responses types are experimental (openai/openai-dotnet#595); file-scoped to contain them here.
#pragma warning disable OPENAI001

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Stamps Responses-API request settings onto every turn served by the Azure OpenAI chat client.
/// </summary>
internal static class AzureOpenAIDefaults
{
    #region Private methods

    // Strictly true: a request assembled outside ConversationService must not turn a stray value into a
    // reasoning request the deployment rejects.
    private static bool IsReasoningRequested(ChatOptions options) =>
        options.AdditionalProperties?.TryGetValue(ChatRequestProperties.IsReasoningEnabled, out var value) == true
        && value is true;

    private static ResponseReasoningSummaryVerbosity ParseSummary(string summary) =>
        summary.ToLowerInvariant() switch
        {
            "auto" => ResponseReasoningSummaryVerbosity.Auto,
            "concise" => ResponseReasoningSummaryVerbosity.Concise,
            "detailed" => ResponseReasoningSummaryVerbosity.Detailed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(summary), summary, "Unknown Azure OpenAI reasoning summary verbosity.")
        };

    private static ResponseReasoningEffortLevel ParseEffort(string effort) =>
        effort.ToLowerInvariant() switch
        {
            "minimal" => ResponseReasoningEffortLevel.Minimal,
            "low" => ResponseReasoningEffortLevel.Low,
            "medium" => ResponseReasoningEffortLevel.Medium,
            "high" => ResponseReasoningEffortLevel.High,
            _ => throw new ArgumentOutOfRangeException(
                nameof(effort), effort, "Unknown Azure OpenAI reasoning effort level.")
        };

    #endregion

    #region Public static methods

    /// <summary>Creates the <see cref="ChatOptions"/> callback for the Azure OpenAI provider.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="settings"/> carries a reasoning summary or effort level the parsers do not know.
    /// </exception>
    /// <remarks>
    /// The keyed client is built lazily, so a parse failure would surface on the first conversation, not
    /// at startup; <c>AzureOpenAIDefaultsTests</c> pins every value the validators accept.
    /// </remarks>
    public static Action<ChatOptions> Create(AzureOpenAIOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var effort = ParseEffort(settings.ReasoningEffort);
        var summary = settings.IsReasoningSummaryRequested
            ? ParseSummary(settings.ReasoningSummary)
            : (ResponseReasoningSummaryVerbosity?)null;

        return options =>
        {
            var wantsReasoning = IsReasoningRequested(options);

            // Cleared: the Responses adapter sends any id not starting with "conv_" as previous_response_id.
            // Safe only because the turn is stateless (below): with storage on, FunctionInvokingChatClient
            // passes a response id down and clearing it would fail every tool-calling turn.
            // UseOpenTelemetry, outermost, has already recorded it as gen_ai.conversation.id.
            options.ConversationId = null;

            if (!wantsReasoning)
            {
                // The adapter applies ChatOptions.Reasoning with ??=, so a caller setting it on every turn
                // would put reasoning on the wire for a deployment that rejects it.
                options.Reasoning = null;
            }

            // Only when the caller has not supplied one, so a single turn can still pass its own parameters.
            options.RawRepresentationFactory ??= _ =>
            {
                // Model, input, and tools are overlaid by the bridge from ChatOptions; only what it
                // does not derive belongs here.
                var raw = new CreateResponseOptions
                {
                    // Off deliberately: Responses stores prompts and answers for 30 days by default,
                    // and the transcript is the system of record. Statelessness also keeps the whole
                    // exchange in each request, which makes the clear above safe.
                    StoredOutputEnabled = false
                };

                if (!wantsReasoning)
                {
                    return raw;
                }

                var reasoning = new ResponseReasoningOptions { ReasoningEffortLevel = effort };

                // Left unset to decline a summary: the API has no "none" verbosity.
                if (summary is not null)
                {
                    reasoning.ReasoningSummaryVerbosity = summary;
                }

                raw.ReasoningOptions = reasoning;

                // Stateless mode: the service cannot look up a replayed reasoning item, so it travels with
                // the request, or a reasoning model rejects its own item once a turn calls a tool.
                raw.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);

                return raw;
            };
        };
    }

    #endregion
}
