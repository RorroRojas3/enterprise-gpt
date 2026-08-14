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
}
