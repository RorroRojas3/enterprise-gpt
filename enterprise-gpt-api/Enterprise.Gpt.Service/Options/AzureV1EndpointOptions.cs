namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// The connection settings shared by every provider reached through Azure's OpenAI-compatible v1
/// API, bound from that provider's configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// Two providers sit on this endpoint and differ only in the surface they call —
/// <see cref="AzureOpenAIOptions"/> uses the Responses API and supports reasoning,
/// <see cref="AzureAIFoundryOptions"/> uses Chat Completions and does not. The URL handling is
/// identical for both, and <see cref="IsUrlResourceRoot"/> in particular encodes a subtlety that is
/// easy to get wrong twice, so it is stated once here.
/// </para>
/// <para>
/// Deliberately free of OpenAI SDK types so the service layer takes no OpenAI package reference;
/// derived types carry provider settings as strings and turn them into SDK types where the client is
/// registered, the same split that keeps AWS and Anthropic types out of this project.
/// </para>
/// </remarks>
public abstract class AzureV1EndpointOptions
{
    /// <summary>
    /// The path segment appended to <see cref="Url"/> to reach the OpenAI-compatible v1 API.
    /// </summary>
    /// <remarks>
    /// A constant because two rules key on it: <see cref="V1Endpoint"/> appends it, and the startup
    /// validator rejects a <see cref="Url"/> that already carries it.
    /// </remarks>
    public const string V1Path = "openai/v1/";

    /// <summary>
    /// Gets or sets the resource root, for example
    /// <c>https://my-resource.services.ai.azure.com/</c>.
    /// </summary>
    /// <remarks>
    /// The resource root, <em>not</em> the v1 endpoint — <see cref="V1Endpoint"/> derives that.
    /// Azure accepts both the <c>services.ai.azure.com</c> and <c>openai.azure.com</c> hosts for the
    /// OpenAI-compatible API, so either form works here.
    /// </remarks>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the resource API key.
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
    /// Gets the OpenAI-compatible v1 endpoint the client is built against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived rather than configured so an existing deployment needs no new setting. The trailing
    /// slash on <see cref="V1Path"/> is load-bearing: the SDK resolves relative request paths
    /// against this URI, and without it the last segment would be replaced instead of extended.
    /// </para>
    /// <para>
    /// The guard is <see cref="IsUrlAbsolute"/> rather than letting <see cref="Uri"/> reject the
    /// string itself: on Unix, <c>new Uri("/openai/v1/")</c> succeeds as an implicit file path, so an
    /// unset or path-shaped URL would build <c>file:///openai/v1/</c> on the Linux hosts this runs on
    /// while throwing on a Windows dev box.
    /// </para>
    /// </remarks>
    /// <exception cref="UriFormatException">
    /// <see cref="Url"/> is not an absolute HTTP or HTTPS URI.
    /// </exception>
    public Uri V1Endpoint =>
        IsUrlAbsolute
            ? new Uri(new Uri(EnsureTrailingSlash(Url)), V1Path)
            : throw new UriFormatException(
                $"The configured URL '{Url}' is not an absolute http or https URI, so no v1 endpoint can be derived from it.");

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
