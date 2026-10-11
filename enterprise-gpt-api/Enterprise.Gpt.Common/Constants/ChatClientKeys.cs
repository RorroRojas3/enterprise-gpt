namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// The dependency-injection keys the provider-specific <c>IChatClient</c> registrations use.
/// </summary>
/// <remarks>
/// Constants so attributes can use them. They sit beside <see cref="Providers"/> because
/// <see cref="Providers.ServiceKeys"/> maps provider ids onto them.
/// </remarks>
public static class ChatClientKeys
{
    /// <summary>Azure OpenAI, over the Responses API. The only provider that serves reasoning.</summary>
    public const string AzureOpenAI = "azure-open-ai";

    /// <summary>Azure AI Foundry, over Chat Completions. No reasoning.</summary>
    public const string AzureAIFoundry = "azure-ai-foundry";

    public const string AmazonBedrock = "amazonbedrock";

    public const string Anthropic = "anthropic";

    /// <summary>
    /// The File Agent's own client, over the same Responses route as <see cref="AzureOpenAI"/>.
    /// </summary>
    /// <remarks>
    /// Not a provider. A separate client gives the agent its own function-invocation bounds, which are
    /// instance settings rather than request options, and leaves tool tracking to the calling turn.
    /// </remarks>
    public const string FileAgent = "file-agent";
}
