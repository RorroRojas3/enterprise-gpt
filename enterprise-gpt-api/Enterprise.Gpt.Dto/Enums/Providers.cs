using System.Collections.Frozen;

namespace Enterprise.Gpt.Dto.Enums
{
    /// <summary>
    /// Contains unique identifiers for different AI service providers.
    /// </summary>
    public static class Providers
    {
        /// <summary>
        /// Azure AI Foundry. This value must match the seeded <c>Core.Ref.Provider</c> row exactly;
        /// out-of-band data migrations must use it verbatim.
        /// </summary>
        public static readonly Guid AzureOpenAI = new("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0");

        /// <summary>
        /// Amazon Bedrock. This value must match the seeded <c>Core.Ref.Provider</c> row exactly;
        /// out-of-band data migrations must use it verbatim.
        /// </summary>
        public static readonly Guid AmazonBedrock = new("8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812");

        /// <summary>
        /// Anthropic, called directly rather than through a cloud reseller. This value must match the
        /// seeded <c>Core.Ref.Provider</c> row exactly; out-of-band data migrations must use it
        /// verbatim.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="AmazonBedrock"/> even though both serve Claude: they differ in
        /// billing, model availability, credential shape, and the identifier format a model carries in
        /// <c>DeploymentName</c>, so a model row has to name which one serves it.
        /// </remarks>
        public static readonly Guid Anthropic = new("6f93ec17-e981-409f-a523-700584f1e7d6");

        /// <summary>
        /// The dependency-injection key each provider's chat client is registered under, keyed by
        /// provider id.
        /// </summary>
        /// <remarks>
        /// The single source of truth shared by the registrations in <c>Program.cs</c> and the runtime
        /// lookup in <c>ChatClientResolver</c>. A provider absent from this map has no chat client and
        /// cannot serve a conversation, whatever rows exist in <c>Core.Ref.Provider</c>.
        /// </remarks>
        public static readonly FrozenDictionary<Guid, string> ServiceKeys = new Dictionary<Guid, string>
        {
            [AzureOpenAI] = ChatClientKeys.AzureAIFoundry,
            [AmazonBedrock] = ChatClientKeys.AmazonBedrock,
            [Anthropic] = ChatClientKeys.Anthropic
        }.ToFrozenDictionary();
    }
}
