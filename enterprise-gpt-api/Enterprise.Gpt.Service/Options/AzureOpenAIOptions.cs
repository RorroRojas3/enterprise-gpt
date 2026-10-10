using System.Collections.Frozen;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Connection settings for the Azure OpenAI chat and embedding clients, bound from the
/// <c>AzureOpenAI</c> configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// This is the Responses-API provider: it is the only one that can serve a reasoning model, because
/// reasoning summaries exist on no other surface. <see cref="AzureAIFoundryOptions"/> is its
/// Chat-Completions counterpart on the same endpoint.
/// </para>
/// <para>
/// Unlike <see cref="AmazonBedrockOptions"/>, <see cref="AnthropicOptions"/> and
/// <see cref="AzureAIFoundryOptions"/> there is no <c>Enabled</c> flag: this provider always serves
/// the default model and owns the embedding generator, so a deployment missing its settings is
/// broken rather than opted out. Before this type existed the four keys were read with raw
/// <c>GetValue&lt;string&gt;</c> and a blank URL became <c>new Uri("")</c> inside a lazy client
/// factory — a startup mistake that surfaced on the first conversation instead of at startup.
/// </para>
/// </remarks>
public sealed class AzureOpenAIOptions : AzureV1EndpointOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "AzureOpenAI";

    /// <summary>
    /// The values <see cref="ReasoningSummary"/> accepts, compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// <c>none</c> is this application's own value, not the service's: it means "ask for no summary
    /// at all", which is how a deployment turns reasoning summaries off globally without editing
    /// every model row.
    /// </remarks>
    public static readonly FrozenSet<string> ReasoningSummaries =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "auto", "concise", "detailed", "none");

    /// <summary>
    /// The values <see cref="ReasoningEffort"/> accepts, compared case-insensitively.
    /// </summary>
    public static readonly FrozenSet<string> ReasoningEfforts =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "minimal", "low", "medium", "high");

    /// <summary>
    /// Gets or sets the embedding deployment name used to index and search documents.
    /// </summary>
    /// <remarks>
    /// It must natively return 1536-dimension vectors, because the stored column is fixed at that
    /// width, and it also selects the tokenizer the chunker uses. Changing it invalidates every
    /// stored embedding.
    /// </remarks>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how verbose a reasoning summary to request: <c>auto</c>, <c>concise</c>,
    /// <c>detailed</c>, or <c>none</c> to request none at all.
    /// </summary>
    /// <remarks>
    /// Applies only to models whose catalog row sets <c>IsReasoningEnabled</c>; every other model is
    /// called without reasoning options, because a deployment that does not support them rejects
    /// the request outright.
    /// </remarks>
    public string ReasoningSummary { get; set; } = "auto";

    /// <summary>
    /// Gets or sets how much reasoning the model applies: <c>minimal</c>, <c>low</c>,
    /// <c>medium</c>, or <c>high</c>.
    /// </summary>
    /// <remarks>
    /// Applies to every reasoning-enabled model in the catalog, because a model row carries no
    /// per-model override. Raising it buys depth at the cost of latency and tokens — reasoning
    /// tokens are billed as output tokens.
    /// </remarks>
    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>
    /// Gets a value indicating whether a reasoning summary is wanted at all.
    /// </summary>
    public bool IsReasoningSummaryRequested =>
        !string.Equals(ReasoningSummary, "none", StringComparison.OrdinalIgnoreCase);
}
