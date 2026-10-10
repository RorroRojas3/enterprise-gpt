namespace Enterprise.Gpt.Dto.Enums;

/// <summary>
/// The dependency-injection keys the provider-specific <c>IChatClient</c> registrations use.
/// </summary>
/// <remarks>
/// Constants rather than a lookup so they can be used in attributes and in the registration calls
/// themselves. They sit beside <see cref="Providers"/> because <see cref="Providers.ServiceKeys"/>
/// maps provider ids onto them, and that map has to live with the ids it keys on.
/// </remarks>
public static class ChatClientKeys
{
    /// <summary>Azure OpenAI, over the Responses API. The only provider that serves reasoning.</summary>
    public const string AzureOpenAI = "azure-open-ai";

    /// <summary>Azure AI Foundry, over Chat Completions. No reasoning.</summary>
    public const string AzureAIFoundry = "azure-ai-foundry";

    /// <summary>Amazon Bedrock.</summary>
    public const string AmazonBedrock = "amazonbedrock";

    /// <summary>Anthropic.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>
    /// The File Agent's own client, over the same Responses route as <see cref="AzureOpenAI"/>.
    /// </summary>
    /// <remarks>
    /// Not a provider: no <c>Core.Ref.Provider</c> row maps to this key, and nothing resolves it
    /// through <see cref="Providers.ServiceKeys"/>. It exists because the agent needs its own
    /// function-invocation bounds, which are instance settings on a client rather than per-request
    /// options, and because tool tracking belongs to the turn that calls the agent rather than to the
    /// agent's own pipeline.
    /// </remarks>
    public const string FileAgent = "file-agent";
}
