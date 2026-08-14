using System.Collections.Frozen;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Connection settings for the Azure AI Foundry chat and embedding clients, bound from the
/// <c>AzureAIFoundry</c> configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="AmazonBedrockOptions"/> and <see cref="AnthropicOptions"/> there is no
/// <c>Enabled</c> flag: this provider always serves the default model, so a deployment missing its
/// settings is broken rather than opted out. Before this type existed the four keys were read with
/// raw <c>GetValue&lt;string&gt;</c> and a blank URL became <c>new Uri("")</c> inside a lazy client
/// factory — a startup mistake that surfaced on the first conversation instead of at startup.
/// </para>
/// <para>
/// Deliberately free of OpenAI SDK types so the service layer takes no OpenAI package reference:
/// <see cref="ReasoningSummary"/> and <see cref="ReasoningEffort"/> are carried as strings and
/// turned into SDK types where the client is registered, the same split that keeps AWS and
/// Anthropic types out of this project.
/// </para>
/// </remarks>
public sealed class AzureAIFoundryOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "AzureAIFoundry";

    /// <summary>
    /// The path segment appended to <see cref="Url"/> to reach the OpenAI-compatible v1 API.
    /// </summary>
    /// <remarks>
    /// A constant because two rules key on it: <see cref="V1Endpoint"/> appends it, and the startup
    /// validator rejects a <see cref="Url"/> that already carries it.
    /// </remarks>
    public const string V1Path = "openai/v1/";

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
    /// Gets or sets the Foundry resource root, for example
    /// <c>https://my-resource.services.ai.azure.com/</c>.
    /// </summary>
    /// <remarks>
    /// The resource root, <em>not</em> the v1 endpoint — <see cref="V1Endpoint"/> derives that.
    /// Azure accepts both the <c>services.ai.azure.com</c> and <c>openai.azure.com</c> hosts for the
    /// OpenAI-compatible API, so either form works here.
    /// </remarks>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Foundry resource API key.
    /// </summary>
    /// <remarks>
    /// Keep it in user secrets locally and Key Vault when deployed, never in
    /// <c>appsettings.json</c>.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the deployment name used when a request does not carry one.
    /// </summary>
    /// <remarks>
    /// Every chat turn sets the model explicitly from the catalog, so this is only the fallback the
    /// SDK requires to construct a client.
    /// </remarks>
    public string DefaultModel { get; set; } = string.Empty;

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

    /// <summary>
    /// Gets the OpenAI-compatible v1 endpoint the chat client is built against.
    /// </summary>
    /// <remarks>
    /// Derived rather than configured so an existing deployment needs no new setting. The trailing
    /// slash on <see cref="V1Path"/> is load-bearing: the SDK resolves relative request paths
    /// against this URI, and without it the last segment would be replaced instead of extended.
    /// </remarks>
    /// <exception cref="UriFormatException"><see cref="Url"/> is not an absolute URI.</exception>
    public Uri V1Endpoint => new(new Uri(EnsureTrailingSlash(Url)), V1Path);

    /// <summary>
    /// Gets a value indicating whether <see cref="Url"/> is an absolute HTTP or HTTPS URI.
    /// </summary>
    public bool IsUrlAbsolute =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Gets a value indicating whether <see cref="Url"/> is the resource root rather than a path
    /// beneath it.
    /// </summary>
    /// <remarks>
    /// Configuring any part of the v1 path here would have it appended twice, and the resulting 404
    /// names neither the setting nor the cause. The test is on the <em>path</em>, not the whole
    /// string, because the perfectly correct <c>https://x.openai.azure.com/</c> has "openai" in its
    /// host — and it catches a bare <c>/openai</c> as well as the full <c>/openai/v1</c>, since
    /// half the path repeated is the same 404 as all of it.
    /// </remarks>
    public bool IsUrlResourceRoot =>
        !Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        || !uri.AbsolutePath.Contains("openai", StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : $"{url}/";
}
