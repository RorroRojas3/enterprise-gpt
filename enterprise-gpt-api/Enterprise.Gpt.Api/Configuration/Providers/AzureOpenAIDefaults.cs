using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

// OPENAI001: every Responses request type is still marked experimental in OpenAI 2.12.0, and it
// remains so at 2.13.0 — the newest release folded the Computer-Use OPENAICUA001 code into this one
// rather than retiring it (openai/openai-dotnet#595 tracks the graduation). No package bump removes
// this. The suppression is file-scoped rather than project-wide precisely because this file is the
// only place those types are allowed to appear — the same containment that keeps them out of the
// Service project, and the reason the sibling Chat Completions provider needs no suppression at all.
// A second file reaching for them has to make the same declaration deliberately.
#pragma warning disable OPENAI001

namespace Enterprise.Gpt.Api.Chat;

/// <summary>
/// Builds the callback that stamps Responses-API request settings onto every turn served by the
/// Azure OpenAI chat client.
/// </summary>
/// <remarks>
/// Kept out of <c>Program.cs</c> for the same reason as <see cref="AnthropicChatDefaults"/>:
/// confining the SDK's request types to one file keeps them out of the provider-agnostic
/// <c>ChatOptions</c> that <c>ConversationService</c> builds. Contrast
/// <see cref="AzureAIFoundryChatDefaults"/>, which reaches the same resource over Chat Completions
/// and so touches no experimental type.
/// </remarks>
internal static class AzureOpenAIChatDefaults
{
    /// <summary>
    /// Creates the <see cref="ChatOptions"/> callback for the Azure OpenAI provider.
    /// </summary>
    /// <param name="settings">The validated Azure OpenAI settings.</param>
    /// <returns>A callback suitable for <c>ConfigureOptionsChatClient</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="settings"/> carries a reasoning summary or effort level the parsers do not
    /// know. The startup validators reject those first, and <c>AzureOpenAIChatDefaultsTests</c>
    /// pins every value they accept — the two vocabularies drifting apart is what this guards.
    /// </exception>
    /// <remarks>
    /// Both values are parsed once here rather than per request. Note that the keyed chat client is
    /// built lazily on first resolution, so a parse failure would surface on the first conversation
    /// rather than at startup; the tests, not the validators, are what keep that unreachable.
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

            // Cleared, and this is not housekeeping. Chat Completions ignored ConversationId; the
            // Responses adapter does not — `OpenAIClientExtensions.IsConversationId` treats an id
            // beginning with "conv_" as a conversation and *anything else* as a response id, which
            // it then sends as `previous_response_id`. ConversationService sets this to the
            // conversation's own GUID, so leaving it would ask the service to continue a response
            // it never issued.
            //
            // Clearing it is only safe because the turn is stateless (below). With storage on, the
            // adapter hands back the service's own response id, `FunctionInvokingChatClient` — which
            // sits *above* this client — reads that as "the service is tracking history", drops the
            // local messages, and passes the id down for the next iteration. Clearing it there would
            // send a bare `function_call_output` with nothing to attach it to, and every
            // tool-calling turn would fail from its second iteration.
            //
            // This client is the innermost of the chain, so UseOpenTelemetry — which wraps outermost
            // — has already recorded the id as gen_ai.conversation.id by the time it is cleared.
            options.ConversationId = null;

            if (!wantsReasoning)
            {
                // Belt and braces, and worth the two lines. `ChatOptions.Reasoning` is
                // Microsoft.Extensions.AI's provider-agnostic reasoning request, and the adapter
                // applies it with `??=` — so it fills in exactly the raw options this path
                // deliberately leaves empty, and a caller that sets it for every turn would put
                // reasoning on the wire for a deployment that rejects the whole request for it.
                // The catalog gate belongs here, in the one place that knows this provider's rule,
                // rather than resting on every call site remembering it.
                options.Reasoning = null;
            }

            // Assigned only when the caller has not supplied one, so a call site that needs
            // different parameters for a single turn keeps the escape hatch
            // Microsoft.Extensions.AI provides.
            options.RawRepresentationFactory ??= _ =>
            {
                // Model, input, and tools are overlaid by the bridge from ChatOptions; only what it
                // does not derive belongs here.
                var raw = new CreateResponseOptions
                {
                    // The Responses API stores prompts and answers for 30 days by default; Chat
                    // Completions stored nothing. Preserving that is a deliberate decision, not an
                    // omission — this turn already carries the user's prompt, the project's
                    // instructions and any retrieved document passages, and the conversation's own
                    // transcript is the system of record. It is also what keeps the whole exchange
                    // in each request, which is what makes the clear above safe.
                    StoredOutputEnabled = false
                };

                if (!wantsReasoning)
                {
                    return raw;
                }

                var reasoning = new ResponseReasoningOptions { ReasoningEffortLevel = effort };

                // Left unset rather than set to a "none" value: the API has no such verbosity, and
                // omitting the property is how a request declines a summary while still asking the
                // model to reason.
                if (summary is not null)
                {
                    reasoning.ReasoningSummaryVerbosity = summary;
                }

                raw.ReasoningOptions = reasoning;

                // Required by stateless mode: with nothing stored, the reasoning item a later
                // iteration replays is one the service cannot look up, so it has to travel with the
                // request. Without this a reasoning model rejects its own replayed item the moment a
                // turn calls a tool.
                raw.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);

                return raw;
            };
        };
    }

    /// <summary>
    /// Reads the catalog's per-model reasoning flag off the request.
    /// </summary>
    /// <remarks>
    /// Absent means no. Only <c>ConversationService</c> writes this key, and it writes a
    /// <see cref="bool"/>, but a request assembled anywhere else must not turn a stray value into a
    /// reasoning request that the deployment then rejects.
    /// </remarks>
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
}
