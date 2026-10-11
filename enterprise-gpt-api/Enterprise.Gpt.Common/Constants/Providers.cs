using System.Collections.Frozen;

namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// Contains unique identifiers for different AI service providers.
/// </summary>
/// <remarks>
/// Each id must match its seeded <c>Core.Ref.Provider</c> row exactly; out-of-band data migrations use them
/// verbatim.
/// </remarks>
public static class Providers
{
    /// <summary>Azure OpenAI over the Responses API: the only surface with reasoning summaries.</summary>
    public static readonly Guid AzureOpenAI = new("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0");

    /// <summary>
    /// Azure AI Foundry over Chat Completions, without reasoning, on the same resource as Azure OpenAI.
    /// </summary>
    /// <remarks>A row here that sets <c>IsReasoningEnabled</c> has it discarded at request time.</remarks>
    public static readonly Guid AzureAIFoundry = new("b7d4e0c3-5a18-4f92-9c6e-2d31f8a70b45");

    public static readonly Guid AmazonBedrock = new("8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812");

    /// <summary>Anthropic, called directly rather than through a cloud reseller.</summary>
    /// <remarks>
    /// Distinct from <see cref="AmazonBedrock"/> though both serve Claude: billing, availability, credentials
    /// and the <c>DeploymentName</c> id format differ.
    /// </remarks>
    public static readonly Guid Anthropic = new("6f93ec17-e981-409f-a523-700584f1e7d6");

    /// <summary>The DI key each provider's chat client is registered under, keyed by provider id.</summary>
    /// <remarks>
    /// Shared by <c>ChatClientsConfiguration</c> and <c>ChatClientResolver</c>. A provider absent here cannot
    /// serve a conversation, whatever rows exist in <c>Core.Ref.Provider</c>.
    /// </remarks>
    public static readonly FrozenDictionary<Guid, string> ServiceKeys = new Dictionary<Guid, string>
    {
        [AzureOpenAI] = ChatClientKeys.AzureOpenAI,
        [AzureAIFoundry] = ChatClientKeys.AzureAIFoundry,
        [AmazonBedrock] = ChatClientKeys.AmazonBedrock,
        [Anthropic] = ChatClientKeys.Anthropic
    }.ToFrozenDictionary();
}
