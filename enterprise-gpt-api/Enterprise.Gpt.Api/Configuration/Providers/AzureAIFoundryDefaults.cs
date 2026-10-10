using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Chat;

/// <summary>
/// Builds the callback that stamps Chat Completions request settings onto every turn served by the
/// Azure AI Foundry chat client.
/// </summary>
/// <remarks>
/// The Chat Completions counterpart to <see cref="AzureOpenAIChatDefaults"/>. It touches no OpenAI
/// SDK request type at all — everything it needs is on the provider-agnostic
/// <see cref="ChatOptions"/> — which is why, unlike its sibling, this file carries no
/// <c>OPENAI001</c> suppression.
/// </remarks>
internal static class AzureAIFoundryChatDefaults
{
    /// <summary>
    /// Creates the <see cref="ChatOptions"/> callback for the Azure AI Foundry provider.
    /// </summary>
    /// <returns>A callback suitable for <c>ConfigureOptionsChatClient</c>.</returns>
    /// <remarks>
    /// Takes no settings: this provider has nothing per-deployment to stamp onto a request. It is
    /// still a factory rather than a bare lambda so the registration in <c>Program.cs</c> reads the
    /// same as every other provider's.
    /// </remarks>
    public static Action<ChatOptions> Create()
    {
        return options =>
        {
            // Cleared unconditionally, and this is not defensive. ConversationService populates
            // ChatOptions.Reasoning from the catalog's IsReasoningEnabled flag, and the Chat
            // Completions adapter translates it into `reasoning_effort` on the wire — which a
            // deployment reached over this surface rejects outright. Reasoning is a Responses-API
            // feature, so a model row here cannot mean it whatever it says; AzureAIFoundryOptions
            // carries no reasoning settings for the same reason.
            //
            // The catalog gate belongs here, in the one place that knows this provider's rule,
            // rather than resting on every call site remembering it.
            options.Reasoning = null;

            // ConversationId is deliberately left alone, and the asymmetry with
            // AzureOpenAIChatDefaults is the point: that one has to clear it because the Responses
            // adapter reads any id not beginning with "conv_" as a response id and sends it as
            // previous_response_id. Chat Completions is stateless and the adapter has nowhere to put
            // it, so there is nothing here to defend against and nothing to clear.
        };
    }
}
