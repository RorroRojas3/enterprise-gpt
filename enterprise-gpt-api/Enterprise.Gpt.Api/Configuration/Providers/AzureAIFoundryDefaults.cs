using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Stamps Chat Completions request settings onto every turn served by the Azure AI Foundry chat client.
/// </summary>
internal static class AzureAIFoundryDefaults
{
    #region Public static methods

    /// <summary>Creates the <see cref="ChatOptions"/> callback for the Azure AI Foundry provider.</summary>
    /// <remarks>A factory with no settings only so it registers like every other provider.</remarks>
    public static Action<ChatOptions> Create()
    {
        return options =>
        {
            // Not defensive: the Chat Completions adapter sends the catalog's reasoning flag as
            // reasoning_effort, which this surface rejects. Reasoning is a Responses-API feature.
            options.Reasoning = null;

            // ConversationId is deliberately left alone: unlike the Responses adapter (see
            // AzureOpenAIDefaults), Chat Completions is stateless and has nowhere to send it.
        };
    }

    #endregion
}
