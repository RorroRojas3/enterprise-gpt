namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// Keys the service layer puts on <c>ChatOptions.AdditionalProperties</c> to carry catalog facts a
/// provider-specific client needs.
/// </summary>
/// <remarks>
/// The seam between the provider-agnostic <c>ConversationService</c> and the SDK-aware client
/// registrations in the API project, which cannot see each other's types.
/// </remarks>
public static class ChatRequestProperties
{
    /// <summary>
    /// Whether the model's catalog row asks for a reasoning summary. <see cref="bool"/>.
    /// </summary>
    /// <remarks>
    /// Read only by the Azure OpenAI client; a model without reasoning rejects the options outright. Not
    /// <c>ChatOptions.Reasoning</c>: effort and verbosity are provider configuration here, and that type
    /// cannot express a <c>minimal</c> effort or an <c>auto</c> summary.
    /// </remarks>
    public const string IsReasoningEnabled = "enterprisegpt.reasoning.enabled";
}
