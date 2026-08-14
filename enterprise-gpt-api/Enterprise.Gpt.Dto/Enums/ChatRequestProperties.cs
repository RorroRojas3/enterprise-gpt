namespace Enterprise.Gpt.Dto.Enums;

/// <summary>
/// Keys the service layer puts on <c>ChatOptions.AdditionalProperties</c> to carry catalog facts a
/// provider-specific client needs.
/// </summary>
/// <remarks>
/// The channel exists because the two ends cannot see each other's types. Everything that decides
/// what a turn asks for lives in <c>ConversationService</c>, which is provider-agnostic and takes no
/// SDK package reference; everything that knows how to say it lives beside the client registration
/// in the API project. A string key on the request is the seam between them, and it is spelled once
/// here so neither side can misspell it.
/// </remarks>
public static class ChatRequestProperties
{
    /// <summary>
    /// Whether the model's catalog row asks for a reasoning summary. <see cref="bool"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by the Azure AI Foundry client, which turns it into the Responses API's reasoning
    /// options. Absent or <see langword="false"/> means the turn is sent without them — which is
    /// what a model that does not support reasoning requires, since it rejects the request outright
    /// rather than ignoring the option.
    /// </para>
    /// <para>
    /// Deliberately not <c>ChatOptions.Reasoning</c>, which exists in Microsoft.Extensions.AI and
    /// which this project could reach without any provider SDK. That type carries a request's
    /// reasoning <em>parameters</em> — effort and output verbosity — which are provider
    /// configuration here rather than a catalog fact, and it cannot express two of the values this
    /// application accepts: a <c>minimal</c> effort and an <c>auto</c> summary. This key carries the
    /// one thing the catalog knows, which is whether the deployment tolerates being asked at all.
    /// The consequence is worth stating: it means nothing to the Bedrock and Anthropic clients,
    /// which take their reasoning settings from their own configuration, so the catalog column is
    /// Azure-only until those two gaps close.
    /// </para>
    /// </remarks>
    public const string IsReasoningEnabled = "enterprisegpt.reasoning.enabled";
}
