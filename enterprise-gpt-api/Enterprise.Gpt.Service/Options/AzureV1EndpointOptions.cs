
namespace Enterprise.Gpt.Service.Options;

/// <summary>Connection settings shared by the providers on Azure's OpenAI-compatible v1 API.</summary>
/// <remarks>
/// Free of OpenAI SDK types so the service layer takes no OpenAI package reference; derived types carry
/// strings that become SDK types where the client is registered.
/// </remarks>
public abstract class AzureV1EndpointOptions
{
    public const string V1Path = "openai/v1/";

    /// <summary>
    /// The resource root, not the v1 endpoint, e.g. <c>https://my-resource.services.ai.azure.com/</c>; the
    /// <c>openai.azure.com</c> host works too.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>User secrets locally, Key Vault when deployed, never <c>appsettings.json</c>.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Only the fallback the SDK needs to build a client; every chat turn sets the model from the catalog.
    /// </summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <remarks>
    /// The trailing slash on <see cref="V1Path"/> is load-bearing: the SDK resolves relative paths against
    /// it. Guarded by <see cref="IsUrlAbsolute"/> because on Unix <c>new Uri("/openai/v1/")</c> parses as a
    /// file path instead of throwing.
    /// </remarks>
    /// <exception cref="UriFormatException">
    /// <see cref="Url"/> is not an absolute HTTP or HTTPS URI.
    /// </exception>
    public Uri V1Endpoint =>
        IsUrlAbsolute
            ? new Uri(new Uri(EnsureTrailingSlash(Url)), V1Path)
            : throw new UriFormatException(
                $"The configured URL '{Url}' is not an absolute http or https URI, so no v1 endpoint can be derived from it.");

    public bool IsUrlAbsolute =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Whether the <see cref="Url"/> path is free of <c>openai</c>: any part of the v1 path there is appended
    /// twice into a 404. The path, not the host, which may legitimately contain it.
    /// </summary>
    public bool IsUrlResourceRoot =>
        !Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        || !uri.AbsolutePath.Contains("openai", StringComparison.OrdinalIgnoreCase);

    #region Private methods

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : $"{url}/";

    #endregion
}
