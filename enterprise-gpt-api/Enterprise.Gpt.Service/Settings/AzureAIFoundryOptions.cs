namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Connection settings for the Azure AI Foundry chat client, bound from the <c>AzureAIFoundry</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// The Chat Completions counterpart to <see cref="AzureOpenAIOptions"/>, on the same
/// OpenAI-compatible v1 endpoint. It carries no reasoning settings, and that absence is the design
/// rather than an omission: reasoning <em>parameters</em> — effort and summary verbosity — are a
/// Responses-API request feature, so a model served here cannot be asked for them however the
/// catalog row is configured, and <c>AzureAIFoundryChatDefaults</c> enforces that at request time.
/// </para>
/// <para>
/// That is a statement about the request, not the response. Some Foundry Models — DeepSeek-R1 among
/// them — reason regardless and return that content inline over Chat Completions. Nothing here
/// suppresses it; what this provider cannot do is ask for a structured reasoning summary, which is
/// what the activity timeline's reasoning region renders. A deployment that needs that region filled
/// belongs on <see cref="AzureOpenAIOptions"/>.
/// </para>
/// <para>
/// Gated by <see cref="Enabled"/> like <see cref="AmazonBedrockOptions"/> and
/// <see cref="AnthropicOptions"/>, so a deployment that does not use it needs no settings at all and
/// a half-filled section still fails at startup rather than on the first conversation.
/// </para>
/// </remarks>
public sealed class AzureAIFoundryOptions : AzureV1EndpointOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "AzureAIFoundry";

    /// <summary>
    /// Gets or sets a value indicating whether this provider is registered at all.
    /// </summary>
    /// <remarks>
    /// When false no chat client is registered under <c>ChatClientKeys.AzureAIFoundry</c>, and a
    /// model row pointing at this provider fails with <c>ProviderNotConfiguredException</c> — a 503,
    /// not a 500.
    /// </remarks>
    public bool Enabled { get; set; }
}
