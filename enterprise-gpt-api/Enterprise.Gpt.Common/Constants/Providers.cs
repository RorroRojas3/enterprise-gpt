using System.Collections.Frozen;

namespace Enterprise.Gpt.Dto.Enums
{
    /// <summary>
    /// Contains unique identifiers for different AI service providers.
    /// </summary>
    public static class Providers
    {
        /// <summary>
        /// Azure OpenAI, reached over the Responses API. This value must match the seeded
        /// <c>Core.Ref.Provider</c> row exactly; out-of-band data migrations must use it verbatim.
        /// </summary>
        /// <remarks>
        /// The only provider that can serve a reasoning model: reasoning summaries exist on the
        /// Responses surface and nowhere else. Distinct from <see cref="AzureAIFoundry"/> even
        /// though both reach the same resource over the same v1 endpoint — they differ in the API
        /// surface called, and so in whether <c>IsReasoningEnabled</c> means anything.
        /// </remarks>
        public static readonly Guid AzureOpenAI = new("3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0");

        /// <summary>
        /// Azure AI Foundry, reached over Chat Completions. This value must match the seeded
        /// <c>Core.Ref.Provider</c> row exactly; out-of-band data migrations must use it verbatim.
        /// </summary>
        /// <remarks>
        /// Serves every deployment on the resource — Azure OpenAI models and Foundry Models such as
        /// DeepSeek or Llama alike — without reasoning. A model row here that sets
        /// <c>IsReasoningEnabled</c> has it discarded at request time.
        /// </remarks>
        public static readonly Guid AzureAIFoundry = new("b7d4e0c3-5a18-4f92-9c6e-2d31f8a70b45");

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
            [AzureOpenAI] = ChatClientKeys.AzureOpenAI,
            [AzureAIFoundry] = ChatClientKeys.AzureAIFoundry,
            [AmazonBedrock] = ChatClientKeys.AmazonBedrock,
            [Anthropic] = ChatClientKeys.Anthropic
        }.ToFrozenDictionary();
    }
}
